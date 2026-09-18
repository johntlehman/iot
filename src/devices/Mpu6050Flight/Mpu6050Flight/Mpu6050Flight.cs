using System.Buffers.Binary;
using System.Device.I2c;
using System.Diagnostics;
using System.Numerics;
using Iot.Device.Imu;
using UnitsNet;

namespace Iot.Device.Mpu6050Flight
{
    /// <summary>
    /// Flight-computer view of the MPU-6050. Uses the <see cref="Mpu6050"/> binding from Iot.Device.Bindings for
    /// detection, configuration and gyro calibration, and reads the data registers itself so that scaling and the
    /// temperature formula are correct for the MPU-6050.
    /// </summary>
    /// <remarks>
    /// The binding was written for the MPU-6500/9250 and has these problems on a real MPU-6050:
    /// <list type="bullet">
    /// <item><c>GetAccelerometer()</c> returns m/s², not g, and both it and <c>GetGyroscopeReading()</c> divide the
    /// scale by <c>1 + SampleRateDivider</c>, so readings are wrong unless the divider is 0.</item>
    /// <item><c>GetTemperature()</c> uses the MPU-9250 constants.</item>
    /// <item><c>AccelerometerBandwidth</c> writes ACCEL_CONFIG_2 (0x1D), which the MPU-6050 does not have. The
    /// accelerometer shares the DLPF selected by <c>GyroscopeBandwidth</c>.</item>
    /// <item><c>GyroscopeBandwidth</c> rewrites GYRO_CONFIG with the un-shifted range, so it must be set while the
    /// range is 250 dps.</item>
    /// <item>Calibration writes the accelerometer offsets to MPU-9250 registers (0x77..), which do nothing here, so the
    /// accelerometer bias is applied in software instead.</item>
    /// </list>
    /// </remarks>
    public sealed class Mpu6050Flight : IDisposable
    {
        /// <summary>Default I2C address (AD0 low).</summary>
        public const byte DefaultI2cAddress = Mpu6050.DefaultI2cAddress;

        /// <summary>Secondary I2C address (AD0 high).</summary>
        public const byte SecondI2cAddress = Mpu6050.SecondI2cAddress;

        /// <summary>DLPF for gyro and accel (CONFIG.DLPF_CFG = 1, 184 Hz). Tames motor vibration, keeps the launch step.</summary>
        public const GyroscopeBandwidth Bandwidth = GyroscopeBandwidth.Bandwidth0184Hz;

        /// <summary>Full-scale gyro range. Spinning or tumbling airframes exceed 1000 dps.</summary>
        public const GyroscopeRange GyroRange = GyroscopeRange.Range2000Dps;

        /// <summary>Full-scale accelerometer range. Motor burn on a mid-power rocket commonly exceeds 8 g.</summary>
        public const AccelerometerRange AccelRange = AccelerometerRange.Range16G;

        /// <summary>SMPLRT_DIV. With the DLPF enabled the base rate is 1 kHz, so 4 gives 200 Hz.</summary>
        public const byte SampleRateDivider = 4;

        /// <summary>Resulting output data rate in Hz.</summary>
        public const int SampleRateHz = 1000 / (1 + SampleRateDivider);

        private const byte AccelXoutH = 0x3B;
        private const int BurstLength = 14; // accel(6) + temp(2) + gyro(6)

        private readonly I2cDevice _i2cDevice;
        private readonly Mpu6050 _imu;
        private readonly bool _shouldDispose;

        /// <summary>
        /// Opens the sensor. Resets it, checks WHO_AM_I and applies the flight configuration.
        /// </summary>
        /// <param name="i2cDevice">I2C device at 0x68 or 0x69.</param>
        /// <param name="shouldDispose">True to dispose the I2C device with this object.</param>
        public Mpu6050Flight(I2cDevice i2cDevice, bool shouldDispose = true)
        {
            _i2cDevice = i2cDevice ?? throw new ArgumentNullException(nameof(i2cDevice));
            _shouldDispose = shouldDispose;
            _imu = new Mpu6050(i2cDevice);
            ApplyFlightConfiguration();
        }

        /// <summary>
        /// The underlying binding, for FIFO, wake-on-motion or anything not exposed here.
        /// </summary>
        public Mpu6050 Binding => _imu;

        /// <summary>
        /// Accelerometer bias in g, subtracted from every sample. Set from a previous <see cref="Calibrate"/> run.
        /// </summary>
        public Vector3 AccelerometerBias { get; set; }

        /// <summary>
        /// Gyroscope bias in dps from the last <see cref="Calibrate"/> run. Already programmed into the chip's
        /// offset registers, so it is informational only.
        /// </summary>
        public Vector3 GyroscopeBias { get; private set; }

        /// <summary>Accelerometer sensitivity in g per LSB for the configured range.</summary>
        public float AccelerationScale => _imu.AccelerometerRange switch
        {
            AccelerometerRange.Range02G => 2.0f / 32768.0f,
            AccelerometerRange.Range04G => 4.0f / 32768.0f,
            AccelerometerRange.Range08G => 8.0f / 32768.0f,
            AccelerometerRange.Range16G => 16.0f / 32768.0f,
            _ => throw new InvalidOperationException("Unknown accelerometer range"),
        };

        /// <summary>Gyroscope sensitivity in dps per LSB for the configured range.</summary>
        public float GyroscopeScale => _imu.GyroscopeRange switch
        {
            GyroscopeRange.Range0250Dps => 250.0f / 32768.0f,
            GyroscopeRange.Range0500Dps => 500.0f / 32768.0f,
            GyroscopeRange.Range1000Dps => 1000.0f / 32768.0f,
            GyroscopeRange.Range2000Dps => 2000.0f / 32768.0f,
            _ => throw new InvalidOperationException("Unknown gyroscope range"),
        };

        /// <summary>
        /// Applies the flight configuration (184 Hz DLPF, ±2000 dps, ±16 g, 200 Hz). Safe to call repeatedly.
        /// </summary>
        public void ApplyFlightConfiguration()
        {
            // The binding's bandwidth setter only works while the gyro range is 250 dps (see class remarks),
            // so drop the range first, set the bandwidth, then raise the ranges.
            _imu.GyroscopeRange = GyroscopeRange.Range0250Dps;
            _imu.GyroscopeBandwidth = Bandwidth;
            _imu.GyroscopeRange = GyroRange;
            _imu.AccelerometerRange = AccelRange;
            _imu.SampleRateDivider = SampleRateDivider;
        }

        /// <summary>
        /// Calibrates gyro and accel. The board must be still, with +Z pointing up. Takes a few seconds.
        /// Gyro offsets are written to the chip; the accelerometer bias is stored in <see cref="AccelerometerBias"/>.
        /// The flight configuration is re-applied afterwards because calibration changes ranges and rates.
        /// </summary>
        /// <returns>Gyro bias (dps) and accelerometer bias (g).</returns>
        public (Vector3 GyroscopeBias, Vector3 AccelerometerBias) Calibrate()
        {
            (Vector3 gyroBias, Vector3 accelBias) = _imu.CalibrateGyroscopeAccelerometer();
            GyroscopeBias = gyroBias;
            AccelerometerBias = accelBias;
            ApplyFlightConfiguration();
            return (gyroBias, accelBias);
        }

        /// <summary>
        /// Reads accelerometer, temperature and gyroscope in one I2C burst so all values belong to the same sample.
        /// </summary>
        public ImuSample ReadSample()
        {
            Span<byte> raw = stackalloc byte[BurstLength];
            _i2cDevice.WriteByte(AccelXoutH);
            _i2cDevice.Read(raw);
            long timestamp = Stopwatch.GetTimestamp();

            float accelScale = AccelerationScale;
            float gyroScale = GyroscopeScale;

            Vector3 accel = new(
                BinaryPrimitives.ReadInt16BigEndian(raw) * accelScale,
                BinaryPrimitives.ReadInt16BigEndian(raw.Slice(2)) * accelScale,
                BinaryPrimitives.ReadInt16BigEndian(raw.Slice(4)) * accelScale);

            short rawTemp = BinaryPrimitives.ReadInt16BigEndian(raw.Slice(6));

            Vector3 gyro = new(
                BinaryPrimitives.ReadInt16BigEndian(raw.Slice(8)) * gyroScale,
                BinaryPrimitives.ReadInt16BigEndian(raw.Slice(10)) * gyroScale,
                BinaryPrimitives.ReadInt16BigEndian(raw.Slice(12)) * gyroScale);

            return new ImuSample(
                accel - AccelerometerBias,
                gyro,
                Temperature.FromDegreesCelsius(rawTemp / 340.0 + 36.53), // MPU-6000/6050 datasheet, register map 4.18
                timestamp);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_shouldDispose)
            {
                _imu.Dispose(); // disposes the I2C device
            }
        }
    }
}
