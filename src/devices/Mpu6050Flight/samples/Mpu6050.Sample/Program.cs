using System.Device.I2c;
using System.Numerics;
using Iot.Device.Mpu6050Flight;

Console.WriteLine("=== MPU-6050 Flight IMU Diagnostic Tool ===");

const int busId = 1;

// Address is 0x68 with AD0 low (Adafruit STEMMA QT default), 0x69 with AD0 high.
I2cConnectionSettings settings = new(busId, Mpu6050Flight.DefaultI2cAddress);
I2cDevice device = I2cDevice.Create(settings);

Mpu6050Flight imu;
try
{
    // Resets the chip, checks WHO_AM_I == 0x68 and applies the flight configuration.
    imu = new Mpu6050Flight(device);
    Console.WriteLine("MPU-6050 detected!");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED to detect MPU-6050: {ex.Message}");
    device.Dispose();
    return;
}

using (imu)
{
    Console.WriteLine(
        $"Flight config: accel {imu.Binding.AccelerometerRange}, gyro {imu.Binding.GyroscopeRange}, " +
        $"DLPF {imu.Binding.GyroscopeBandwidth}, {Mpu6050Flight.SampleRateHz} Hz");

    Console.WriteLine("Calibrating. Keep the board still with +Z up for a few seconds...");
    (Vector3 gyroBias, Vector3 accelBias) = imu.Calibrate();
    Console.WriteLine($"Gyro bias  (dps): {gyroBias}  (programmed into the chip)");
    Console.WriteLine($"Accel bias (g):   {accelBias}  (subtracted in software; persist and restore via AccelerometerBias)");

    Console.WriteLine();
    Console.WriteLine("Streaming at 10 Hz. Ctrl+C to stop.");
    Console.WriteLine("Expect ~1 g on the axis pointing up and ~0 dps on all gyro axes at rest.");
    Console.WriteLine();

    while (true)
    {
        ImuSample s = imu.ReadSample();

        Console.WriteLine(
            $"accel g  X {s.Acceleration.X,7:F3} Y {s.Acceleration.Y,7:F3} Z {s.Acceleration.Z,7:F3} | " +
            $"gyro dps X {s.AngularRate.X,8:F2} Y {s.AngularRate.Y,8:F2} Z {s.AngularRate.Z,8:F2} | " +
            $"{s.Temperature.DegreesCelsius:F1} C");

        Thread.Sleep(100);
    }
}
