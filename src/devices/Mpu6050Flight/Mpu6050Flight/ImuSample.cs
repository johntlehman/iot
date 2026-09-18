using System.Numerics;
using UnitsNet;

namespace Iot.Device.Mpu6050Flight
{
    /// <summary>
    /// One coherent accelerometer / gyroscope / temperature sample.
    /// </summary>
    /// <param name="Acceleration">Linear acceleration in g, bias-corrected, sensor axes.</param>
    /// <param name="AngularRate">Angular rate in degrees per second, sensor axes.</param>
    /// <param name="Temperature">Die temperature (MPU-6050 formula).</param>
    /// <param name="Timestamp">Monotonic timestamp from <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>.</param>
    public readonly record struct ImuSample(Vector3 Acceleration, Vector3 AngularRate, Temperature Temperature, long Timestamp);
}
