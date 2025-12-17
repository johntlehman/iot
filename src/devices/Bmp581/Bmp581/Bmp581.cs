using System;
using System.ComponentModel.Design;
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

        public enum PowerMode : byte
        {
            /// <summary>
            /// Standby mode - no measurements
            /// </summary>
            Standby = 0b00,

            /// <summary>
            /// Normal mode - continuous measurements at configured ODR
            /// </summary>
            Normal = 0b01,

            /// <summary>
            /// Forced mode - single measurement then return to standby
            /// </summary>
            Forced = 0b10,

            /// <summary>
            /// Non-Stop mode - continuous measurements without duty cycling
            /// </summary>
            Continuous = 0b11
        }

        /// <summary>
        /// Oversampling multiplier setting, used for both temperature and pressure
        /// </summary>
        public enum OversamplingRate : byte
        {
            OneX = 0b000,
            TwoX = 0b001,
            FourX = 0b010,
            EightX = 0b011,
            SixteenX = 0b100,
            ThirtyTwoX = 0b101,
            SixtyFourX = 0b110,
            OneTwentyEightX = 0b111
        }
        
        // PROPERTIES
        public bool PressureEnabled
        {
            get
            {
                // Read OSR config register
                byte config = Read8BitsFromRegister((byte)Bmp581Register.OSR_CONFIG);
                // Get bit 6 (press_en)
                return (config & (1<<6)) != 0;
            }

            set
            {
                // Read OSR config register
                byte config = Read8BitsFromRegister((byte)Bmp581Register.OSR_CONFIG);

                if (value)
                {
                    config |= 0b0100_0000; // Set bit 6
                }
                else
                {
                    config &= 0b1011_1111; // Clear bit 6
                }

                Write8BitsToRegister((byte)Bmp581Register.OSR_CONFIG, config);
            }
        }

        // === FIELDS ===
        private readonly I2cDevice _i2cDevice;

        // CONSTRUCTOR AND INITIALIZION
        public Bmp581(I2cDevice i2cDevice)
        {
            _i2cDevice = i2cDevice ?? throw new ArgumentNullException(nameof(i2cDevice));
            VerifyChipID();
        }

        private void VerifyChipID()
        {
            byte chipID = Read8BitsFromRegister((byte)Bmp581Register.CHIP_ID);

            if (chipID != ChipIdValue)
            {
                throw new IOException($"Unable to find a chip with id {ChipIdValue}. Found one with id {chipID}");
            }
        }

        // PUBLIC METHODS
        public PowerMode GetPowerMode()
        {
            byte value = Read8BitsFromRegister((byte)Bmp581Register.ODR_CONFIG);
            return (PowerMode)(value & 0b_0000_0011); // Extract bits 0-1
        }

        public void SetPowerMode(PowerMode mode)
        {
            byte current = Read8BitsFromRegister((byte)Bmp581Register.ODR_CONFIG);

            // Clear bits 0-1 (power mode), keep everything else
            current = (byte)((current & 0b_1111_1100) | (byte)mode);

            // Write back
            Span<byte> command = stackalloc byte[] { (byte)Bmp581Register.ODR_CONFIG, current };
            _i2cDevice.Write(command);
        }

        public OversamplingRate GetTempOsr()
        {
            byte value = Read8BitsFromRegister((byte)Bmp581Register.OSR_CONFIG);

            return (OversamplingRate)(value & 0b0000_0111); // Extract bits 0-2
        }

        public void SetTempOsr(OversamplingRate rate)
        {
            byte current = Read8BitsFromRegister((byte)Bmp581Register.OSR_CONFIG);

            // Clear bits 0-2 (temp OSR setting), keep everything else, then set the temp OSR bits
            current = (byte)((current & 0b_1111_1000) | (byte)rate);

            Span<byte> command = stackalloc byte[] {(byte)Bmp581Register.OSR_CONFIG, current};
            _i2cDevice.Write(command);
        }

        public OversamplingRate GetPressOsr()
        {
            byte value = Read8BitsFromRegister((byte)Bmp581Register.OSR_CONFIG);

            return (OversamplingRate)((value & 0b0011_1000) >> 3); // Extract bits 3-5, then right shift 3 places
        }

        public void SetPressOsr(OversamplingRate rate)
        {
            byte current = Read8BitsFromRegister((byte)Bmp581Register.OSR_CONFIG);

            // Clear bits 3-5 (Press OSR config), keep everything else, then set bits 3-5 to desired OSR setting
            current = (byte)((current & 0b1100_0111) | (byte)((int)rate << 3));

            Span<byte> command = stackalloc byte[] { (byte)Bmp581Register.OSR_CONFIG, current };
            _i2cDevice.Write(command);
        }

        /// <summary>
        /// Reads the temperature from the sensor.
        /// </summary>
        /// <returns>Temperature in degrees Celsius.</returns>
        public Temperature ReadTemperature()
        {
            int rawTemp = Read24BitsFromRegister((byte)Bmp581Register.TEMP_DATA_XLSB);

            // Convert using fixed point scaling: raw / 2^16
            double tempCelcius = rawTemp / 65536.0;

            return Temperature.FromDegreesCelsius(tempCelcius);
        }

        /// <summary>
        /// Reads the pressure from the sensor.
        /// </summary>
        /// <returns>Pressure in Pascals.</returns>
        public Pressure ReadPressure()
        {
            int rawPress = Read24BitsFromRegister((byte)Bmp581Register.PRESS_DATA_XLSB);

            // Convert using fixed-point scaling: raw / 2^6
            double pressurePa = rawPress / 64.0;

            return Pressure.FromPascals(pressurePa);
        }
        
        /// <summary>
        /// Calculates altitude based on measured pressure and reference sea-level pressure.
        /// Uses the barometric formula.
        /// </summary>
        /// <param name="seaLevelPressure">Current sea-level pressure at your location (from weather service)</param>
        /// <returns>Altitude as a length object</returns>
        public Length CalculateAltitude(Pressure seaLevelPressure)
        {
            Pressure measuredPressure = ReadPressure();
            // Barometric formula: h = 44330 * (1- (P/P0)^0.1903)
            double altitudeMeters = 44330 * (1.0 - Math.Pow(measuredPressure.Pascals / seaLevelPressure.Pascals, 0.1903));

            return Length.FromMeters(altitudeMeters);
        }

        // PRIVATE I2C HELPER FUNCTIONS
        private byte Read8BitsFromRegister(byte register)
        {
            _i2cDevice.WriteByte(register);
            byte value = _i2cDevice.ReadByte();
            return value;
        }

        private void Write8BitsToRegister(byte register, byte data)
        {
            Span<byte> command = stackalloc byte[] { register, data };
            _i2cDevice.Write(command);
        }

        private int Read24BitsFromRegister(byte registerxlsb)
        {
            Span<byte> data = stackalloc byte[3];
            _i2cDevice.WriteByte(registerxlsb); // Start at XLSB register
            _i2cDevice.Read(data);

            // Combine bytes: data[0]=XLSB, data[1]=LSB, data[2]=MSB
            int raw = (data[2] << 16) | (data[1] << 8 | data[0]);

            // Sign-extend from 24-bit to 32-bit (check bit 23)
            if((raw & 0x800000) != 0)
            {
                raw |= unchecked((int)0xFF000000);
            }

            return raw;
        }

        // PRIVATE CALCULATION HELPERS

        // DISPOSE
        public void Dispose() 
        {
            _i2cDevice.Dispose();
        }

        // NESTED TYPES
        private struct Bmp581Calibrationdata { }
    }
}
