namespace Iot.Device.Bmp581
{
    internal enum Bmp581Register : byte
    {
        CMD = 0x7E,

        OSR_EFF = 0x38,
        // Power mode control
        ODR_CONFIG = 0x37,
        OSR_CONFIG = 0x36,
        OOR_CONFIG = 0x35,
        OOR_RANGE = 0x34,

        DSP_IIR = 0x31,
        DSP_CONFIG = 0x30,
        NVM_DATA_MSB = 0x2D,
        NVM_DATA_LSB = 0x2C,
        NVM_ADDRESS = 0x2B,
        FIFO_DATA = 0x29,
        STATUS = 0x28,
        INT_STATUS = 0x27,
        PRESS_DATA_MSB = 0x22,
        PRESS_DATA_LSB = 0x21,
        PRESS_DATA_XLSB = 0x20,
        TEMP_DATA_MSB = 0x1F,
        TEMP_DATA_LSB = 0x1E,
        TEMP_DATA_XLSB = 0x1D,

        FIFO_SEL = 0x18,
        INT_SOURCE = 0x15,
        INT_CONFIG = 0x14,
        DRIVE_CONFIG = 0x13,

        CHIP_STATUS = 0x11,

        REV_ID = 0x02,
        CHIP_ID = 0x01
    }
}
