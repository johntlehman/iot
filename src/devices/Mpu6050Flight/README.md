# MPU-6050 flight IMU (Adafruit 3886, STEMMA QT)

dotnet/iot already ships an MPU-6050 driver as `Iot.Device.Imu.Mpu6050` in the `Iot.Device.Bindings` NuGet package,
so there is no hand-written register driver here. The binding was written for the MPU-6500/9250 family, though, and
several of its code paths are wrong on a real MPU-6050 (see below). `Mpu6050Flight` wraps it: the binding does
detection, configuration and gyro calibration, and the wrapper does the data-path read and scaling itself.

- `Mpu6050Flight/` – `Mpu6050Flight` wrapper and the `ImuSample` record.
- `samples/Mpu6050.Sample/` – Raspberry Pi console tool: detect, configure, calibrate, stream readings.
- `tests/Mpu6050.Tests/` – xUnit tests that drive the wrapper and binding through an in-memory `FakeI2cDevice`.

## Wiring

STEMMA QT / Qwiic cable to the Pi's I2C bus 1:

| MPU-6050 | Raspberry Pi |
|---|---|
| VIN (red) | 3.3 V |
| GND (black) | GND |
| SDA (blue) | GPIO2 / SDA1 |
| SCL (yellow) | GPIO3 / SCL1 |

Address is `0x68` (`Mpu6050Flight.DefaultI2cAddress`). Tie AD0 high for `0x69` (`SecondI2cAddress`) if the bus already
has a device at `0x68`. Check with `i2cdetect -y 1`.

## Usage

```csharp
using I2cDevice device = I2cDevice.Create(new I2cConnectionSettings(1, Mpu6050Flight.DefaultI2cAddress));
using Mpu6050Flight imu = new(device);          // reset, WHO_AM_I check, flight configuration

(Vector3 gyroBias, Vector3 accelBias) = imu.Calibrate();   // on the pad, still, +Z up
// persist accelBias; on the next boot set imu.AccelerometerBias = accelBias instead of recalibrating

ImuSample s = imu.ReadSample();                 // one 14-byte burst: accel (g), gyro (dps), temperature
```

## Flight configuration

Applied by the constructor and by `ApplyFlightConfiguration()`:

| Setting | Value | Why |
|---|---|---|
| `GyroscopeBandwidth` | 184 Hz (DLPF_CFG = 1) | Also filters the accelerometer on the MPU-6050. Tames motor vibration, keeps the launch step. |
| `GyroscopeRange` | ±2000 dps | Spin-stabilised or tumbling airframes exceed 1000 dps. |
| `AccelerometerRange` | ±16 g | Mid-power motors routinely exceed 8 g at ignition. |
| `SampleRateDivider` | 4 | 1 kHz / (1 + 4) = 200 Hz output rate. |

## Binding problems on a real MPU-6050, and how the wrapper avoids them

1. **Scaling.** `GetAccelerometer()` returns m/s² despite the docs saying g, and both it and `GetGyroscopeReading()`
   divide the scale by `1 + SampleRateDivider`, so values are 5× too small at 200 Hz. `ReadSample()` reads the raw
   registers and scales by `range / 32768` itself.
2. **Temperature.** `GetTemperature()` uses the MPU-9250 constants. `ReadSample()` uses `raw / 340 + 36.53` from the
   MPU-6050 register map.
3. **`AccelerometerBandwidth` does not work.** It writes `ACCEL_CONFIG_2` (0x1D), which the MPU-6050 lacks. Only the
   default passes the setter's read-back check. On this chip the accelerometer shares the DLPF set by
   `GyroscopeBandwidth`, so the wrapper never touches it.
4. **`GyroscopeBandwidth` must be set while the range is 250 dps.** The setter rewrites `GYRO_CONFIG` with the
   un-shifted range value and fails its own read-back otherwise. `ApplyFlightConfiguration()` drops the range, sets the
   bandwidth, then raises the ranges. `BindingQuirk_SettingGyroBandwidthAfterRange_Throws` proves the hazard.
5. **Accelerometer offset registers.** Calibration writes them at the MPU-9250 addresses (0x77..), which do nothing on
   an MPU-6050. The wrapper keeps the returned bias in `AccelerometerBias` and subtracts it in software. Gyro offsets
   (0x13..) are the same on both chips and do take effect.
6. **No data-ready interrupt.** Poll at the flight loop rate, or use `imu.Binding.FifoModes` / `ReadFifo` to batch.

## Build and run

```bash
cd src/devices/Mpu6050Flight
dotnet build
DOTNET_ROLL_FORWARD=Major dotnet test        # env var only needed when the .NET 9 runtime is not installed
dotnet publish samples/Mpu6050.Sample -c Release -r linux-arm64 --self-contained false -o out
scp -r out pi@rocket.local:~/mpu6050
ssh pi@rocket.local ~/mpu6050/Mpu6050.Sample
```

At rest the axis pointing up reads about +1 g and the gyro axes about 0 dps after calibration.
