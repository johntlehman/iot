namespace Iot.Device.Rfm95w
{
    /// <summary>
    /// SX1276/77/78/79 register map in LoRa mode (datasheet rev. 7, table 41).
    /// </summary>
    internal enum Rfm95wRegister : byte
    {
        Fifo = 0x00,
        OpMode = 0x01,
        FrfMsb = 0x06,
        FrfMid = 0x07,
        FrfLsb = 0x08,
        PaConfig = 0x09,
        PaRamp = 0x0A,
        Ocp = 0x0B,
        Lna = 0x0C,
        FifoAddrPtr = 0x0D,
        FifoTxBaseAddr = 0x0E,
        FifoRxBaseAddr = 0x0F,
        FifoRxCurrentAddr = 0x10,
        IrqFlagsMask = 0x11,
        IrqFlags = 0x12,
        RxNbBytes = 0x13,
        ModemStat = 0x18,
        PktSnrValue = 0x19,
        PktRssiValue = 0x1A,
        RssiValue = 0x1B,
        ModemConfig1 = 0x1D,
        ModemConfig2 = 0x1E,
        SymbTimeoutLsb = 0x1F,
        PreambleMsb = 0x20,
        PreambleLsb = 0x21,
        PayloadLength = 0x22,
        MaxPayloadLength = 0x23,
        HopPeriod = 0x24,
        ModemConfig3 = 0x26,
        DetectOptimize = 0x31,
        InvertIq = 0x33,
        DetectionThreshold = 0x37,
        SyncWord = 0x39,
        DioMapping1 = 0x40,
        DioMapping2 = 0x41,
        Version = 0x42,
        PaDac = 0x4D,
    }

    /// <summary>
    /// RegOpMode bits.
    /// </summary>
    [Flags]
    internal enum OpModeBits : byte
    {
        ModeMask = 0b0000_0111,
        LowFrequencyModeOn = 0b0000_1000,
        AccessSharedReg = 0b0100_0000,
        LongRangeMode = 0b1000_0000,
    }

    /// <summary>
    /// RegIrqFlags bits. Writing a 1 clears the flag.
    /// </summary>
    [Flags]
    internal enum IrqFlags : byte
    {
        None = 0,
        CadDetected = 0b0000_0001,
        FhssChangeChannel = 0b0000_0010,
        CadDone = 0b0000_0100,
        TxDone = 0b0000_1000,
        ValidHeader = 0b0001_0000,
        PayloadCrcError = 0b0010_0000,
        RxDone = 0b0100_0000,
        RxTimeout = 0b1000_0000,
        All = 0xFF,
    }
}
