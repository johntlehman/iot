using System.Numerics;
using Iot.Device.Imu;
using Iot.Device.Mpu6050Flight;
using Xunit;

namespace Mpu6050.Tests
{
    public class Mpu6050FlightTests
    {
        private const byte SmplrtDiv = 0x19;
        private const byte Config = 0x1A;
        private const byte GyroConfig = 0x1B;
        private const byte AccelConfig = 0x1C;
        private const byte AccelXoutH = 0x3B;
        private const byte TempOutH = 0x41;
        private const byte GyroXoutH = 0x43;
        private const byte PwrMgmt1 = 0x6B;
        private const byte WhoAmI = 0x75;

        private static FakeI2cDevice CreateFakeMpu6050()
        {
            FakeI2cDevice fake = new();
            fake[WhoAmI] = 0x68;
            return fake;
        }

        private static void SetInt16(FakeI2cDevice fake, byte register, short value)
        {
            fake[register] = (byte)(value >> 8);
            fake[(byte)(register + 1)] = (byte)(value & 0xFF);
        }

        [Fact]
        public void Constructor_ResetsPowersOnAndAppliesFlightConfig()
        {
            FakeI2cDevice fake = CreateFakeMpu6050();

            using Mpu6050Flight imu = new(fake);

            Assert.Contains((PwrMgmt1, (byte)0x80), fake.Writes); // device reset
            Assert.Contains((PwrMgmt1, (byte)0x01), fake.Writes); // clock = PLL gyro X
            Assert.Equal(0x01, fake[Config]);       // DLPF_CFG = 1 -> 184 Hz
            Assert.Equal(0x18, fake[GyroConfig]);   // FS_SEL = 3 -> 2000 dps
            Assert.Equal(0x18, fake[AccelConfig]);  // AFS_SEL = 3 -> 16 g
            Assert.Equal(0x04, fake[SmplrtDiv]);    // 1 kHz / 5 = 200 Hz
            Assert.Equal(200, Mpu6050Flight.SampleRateHz);
            Assert.Equal(GyroscopeBandwidth.Bandwidth0184Hz, imu.Binding.GyroscopeBandwidth);
            Assert.Equal(GyroscopeRange.Range2000Dps, imu.Binding.GyroscopeRange);
            Assert.Equal(AccelerometerRange.Range16G, imu.Binding.AccelerometerRange);
        }

        [Fact]
        public void Constructor_Throws_WhenWhoAmIIsWrong()
        {
            FakeI2cDevice fake = new();
            fake[WhoAmI] = 0x71; // an MPU-9250 answering on the bus

            Assert.Throws<IOException>(() => new Mpu6050Flight(fake));
        }

        [Fact]
        public void ApplyFlightConfiguration_IsIdempotent()
        {
            FakeI2cDevice fake = CreateFakeMpu6050();
            using Mpu6050Flight imu = new(fake);

            imu.ApplyFlightConfiguration();
            imu.ApplyFlightConfiguration();

            Assert.Equal(0x01, fake[Config]);
            Assert.Equal(0x18, fake[GyroConfig]);
            Assert.Equal(0x18, fake[AccelConfig]);
            Assert.Equal(GyroscopeRange.Range2000Dps, imu.Binding.GyroscopeRange);
        }

        [Fact]
        public void Scales_MatchFlightRanges()
        {
            FakeI2cDevice fake = CreateFakeMpu6050();
            using Mpu6050Flight imu = new(fake);

            Assert.Equal(16.0f / 32768.0f, imu.AccelerationScale, precision: 7);
            Assert.Equal(2000.0f / 32768.0f, imu.GyroscopeScale, precision: 7);
        }

        [Fact]
        public void ReadSample_BurstReadsAndScalesCorrectly()
        {
            FakeI2cDevice fake = CreateFakeMpu6050();
            using Mpu6050Flight imu = new(fake);

            SetInt16(fake, AccelXoutH, 0);            // X
            SetInt16(fake, AccelXoutH + 2, -1024);    // Y = -0.5 g at 16 g range
            SetInt16(fake, AccelXoutH + 4, 2048);     // Z = +1 g
            SetInt16(fake, TempOutH, 0);              // 0 raw -> 36.53 C
            SetInt16(fake, GyroXoutH, 16384);         // X = 1000 dps at 2000 dps range
            SetInt16(fake, GyroXoutH + 2, -32768);    // Y = -2000 dps
            SetInt16(fake, GyroXoutH + 4, 164);       // Z ~= 10.01 dps

            ImuSample s = imu.ReadSample();

            Assert.Equal(0.0f, s.Acceleration.X, precision: 5);
            Assert.Equal(-0.5f, s.Acceleration.Y, precision: 5);
            Assert.Equal(1.0f, s.Acceleration.Z, precision: 5);
            Assert.Equal(1000.0f, s.AngularRate.X, precision: 3);
            Assert.Equal(-2000.0f, s.AngularRate.Y, precision: 3);
            Assert.Equal(10.01f, s.AngularRate.Z, precision: 2);
            Assert.Equal(36.53, s.Temperature.DegreesCelsius, precision: 2);
            Assert.NotEqual(0, s.Timestamp);
        }

        [Fact]
        public void ReadSample_Temperature_UsesMpu6050Formula()
        {
            FakeI2cDevice fake = CreateFakeMpu6050();
            using Mpu6050Flight imu = new(fake);
            SetInt16(fake, TempOutH, 3400); // 3400 / 340 + 36.53 = 46.53 C

            ImuSample s = imu.ReadSample();

            Assert.Equal(46.53, s.Temperature.DegreesCelsius, precision: 2);
        }

        [Fact]
        public void ReadSample_SubtractsAccelerometerBias()
        {
            FakeI2cDevice fake = CreateFakeMpu6050();
            using Mpu6050Flight imu = new(fake);
            SetInt16(fake, AccelXoutH + 4, 2048); // Z = 1 g
            imu.AccelerometerBias = new Vector3(0.01f, -0.02f, 0.03f);

            ImuSample s = imu.ReadSample();

            Assert.Equal(-0.01f, s.Acceleration.X, precision: 5);
            Assert.Equal(0.02f, s.Acceleration.Y, precision: 5);
            Assert.Equal(0.97f, s.Acceleration.Z, precision: 5);
        }

        [Fact]
        public void ReadSample_IsSingleBurstFromAccelXoutH()
        {
            FakeI2cDevice fake = CreateFakeMpu6050();
            using Mpu6050Flight imu = new(fake);
            fake.ReadLog.Clear();

            imu.ReadSample();

            (byte register, int length) = Assert.Single(fake.ReadLog);
            Assert.Equal(AccelXoutH, register);
            Assert.Equal(14, length);
        }

        [Fact]
        public void BindingQuirk_SettingGyroBandwidthAfterRange_Throws()
        {
            // Documents why ApplyFlightConfiguration drops the range to 250 dps before setting the bandwidth: the
            // binding's bandwidth setter writes the un-shifted range value into GYRO_CONFIG and fails its own read-back.
            FakeI2cDevice fake = CreateFakeMpu6050();
            using Mpu6050Flight imu = new(fake);

            Assert.Throws<IOException>(() => imu.Binding.GyroscopeBandwidth = GyroscopeBandwidth.Bandwidth0184Hz);
        }

        [Fact]
        public void BindingQuirk_BindingScalesAreWrongWithSampleRateDivider()
        {
            // Documents why ReadSample does its own scaling: the binding divides the LSB scale by (1 + SMPLRT_DIV)
            // and reports acceleration in m/s².
            FakeI2cDevice fake = CreateFakeMpu6050();
            using Mpu6050Flight imu = new(fake);

            float bindingScale = imu.Binding.AccelerationScale;

            Assert.NotEqual(imu.AccelerationScale, bindingScale);
            Assert.Equal(16.0f * 9.807f / 32768.0f / 5.0f, bindingScale, precision: 7);
        }

        [Fact]
        public void Dispose_DisposesI2cDevice_WhenShouldDispose()
        {
            FakeI2cDevice fake = CreateFakeMpu6050();
            Mpu6050Flight imu = new(fake);

            imu.Dispose();

            Assert.True(fake.Disposed);
        }

        [Fact]
        public void Dispose_LeavesI2cDevice_WhenShouldDisposeIsFalse()
        {
            FakeI2cDevice fake = CreateFakeMpu6050();
            Mpu6050Flight imu = new(fake, shouldDispose: false);

            imu.Dispose();

            Assert.False(fake.Disposed);
        }
    }
}
