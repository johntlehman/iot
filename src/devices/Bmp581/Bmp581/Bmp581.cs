using System;
using System.Device.I2c;
using System.IO;
using UnitsNet;

namespace Iot.Device.Bmp581
{
    public class Bmp581 : IDisposable
    {
        // === CONSTANTS ===
        public const byte DefaultI2cAddress = 0x47;
        public const byte SecondaryI2cAddress = 0x46;
        private const byte ChipIdValue = 0x50;

        // === FIELDS ===
        private readonly I2cDevice _i2cDevice;
        private Bmp581Calibrationdata _calibration;

        // CONSTRUCTOR AND INITIALIZION
        public Bmp581(I2cDevice i2cDevice)
        {
            _i2cDevice = i2cDevice;

        }

        public bool VerifyChipID()
        {
            byte chipID = Read8BitsFromRegister((byte)Bmp581Register.CHIPID);
            return chipID == DeviceId;
        }

        private void ReadCalibrationData() { }

        // PRIVATE I2C HELPER FUNCTIONS
        private byte Read8BitsFromRegister(byte register)
        {
            _i2cDevice.WriteByte(register);
            byte value = _i2cDevice.ReadByte();
            return value;
        }

        // DISPOSE
        public void Dispose() 
        {
            // Do something here
        }

        // NESTED TYPES
        private struct Bmp581Calibrationdata { }

    }
}
