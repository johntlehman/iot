namespace Iot.Device.Rfm95w
{
    /// <summary>
    /// Transceiver operating mode (RegOpMode bits 2..0).
    /// </summary>
    public enum OperatingMode : byte
    {
        /// <summary>Lowest power; the only mode in which the LoRa/FSK selection can change.</summary>
        Sleep = 0b000,

        /// <summary>Oscillator running, ready to transmit or receive.</summary>
        Standby = 0b001,

        /// <summary>Frequency synthesis for TX.</summary>
        FrequencySynthesisTx = 0b010,

        /// <summary>Transmitting; returns to Standby when done.</summary>
        Transmit = 0b011,

        /// <summary>Frequency synthesis for RX.</summary>
        FrequencySynthesisRx = 0b100,

        /// <summary>Receive until told otherwise.</summary>
        ReceiveContinuous = 0b101,

        /// <summary>Receive one packet, then Standby.</summary>
        ReceiveSingle = 0b110,

        /// <summary>Channel activity detection.</summary>
        ChannelActivityDetection = 0b111,
    }

    /// <summary>
    /// LoRa spreading factor. Higher = slower but longer range and better sensitivity.
    /// SF6 needs implicit-header mode and is not supported by this driver.
    /// </summary>
    public enum SpreadingFactor : byte
    {
        /// <summary>128 chips/symbol. Fastest.</summary>
        Sf7 = 7,

        /// <summary>256 chips/symbol.</summary>
        Sf8 = 8,

        /// <summary>512 chips/symbol.</summary>
        Sf9 = 9,

        /// <summary>1024 chips/symbol.</summary>
        Sf10 = 10,

        /// <summary>2048 chips/symbol.</summary>
        Sf11 = 11,

        /// <summary>4096 chips/symbol. Slowest, longest range.</summary>
        Sf12 = 12,
    }

    /// <summary>
    /// LoRa signal bandwidth (RegModemConfig1 bits 7..4).
    /// </summary>
    public enum LoRaBandwidth : byte
    {
        /// <summary>7.8 kHz.</summary>
        Bw7_8kHz = 0,

        /// <summary>10.4 kHz.</summary>
        Bw10_4kHz = 1,

        /// <summary>15.6 kHz.</summary>
        Bw15_6kHz = 2,

        /// <summary>20.8 kHz.</summary>
        Bw20_8kHz = 3,

        /// <summary>31.25 kHz.</summary>
        Bw31_25kHz = 4,

        /// <summary>41.7 kHz.</summary>
        Bw41_7kHz = 5,

        /// <summary>62.5 kHz.</summary>
        Bw62_5kHz = 6,

        /// <summary>125 kHz. The usual default.</summary>
        Bw125kHz = 7,

        /// <summary>250 kHz.</summary>
        Bw250kHz = 8,

        /// <summary>500 kHz. Fastest, least sensitive.</summary>
        Bw500kHz = 9,
    }

    /// <summary>
    /// Forward error correction coding rate (RegModemConfig1 bits 3..1).
    /// </summary>
    public enum CodingRate : byte
    {
        /// <summary>4/5: least overhead.</summary>
        FourFifths = 1,

        /// <summary>4/6.</summary>
        FourSixths = 2,

        /// <summary>4/7.</summary>
        FourSevenths = 3,

        /// <summary>4/8: most robust.</summary>
        FourEighths = 4,
    }

    /// <summary>
    /// Helpers for the enums above.
    /// </summary>
    public static class LoRaParameters
    {
        /// <summary>
        /// Bandwidth in Hz.
        /// </summary>
        public static double ToHertz(this LoRaBandwidth bandwidth) => bandwidth switch
        {
            LoRaBandwidth.Bw7_8kHz => 7_800,
            LoRaBandwidth.Bw10_4kHz => 10_400,
            LoRaBandwidth.Bw15_6kHz => 15_600,
            LoRaBandwidth.Bw20_8kHz => 20_800,
            LoRaBandwidth.Bw31_25kHz => 31_250,
            LoRaBandwidth.Bw41_7kHz => 41_700,
            LoRaBandwidth.Bw62_5kHz => 62_500,
            LoRaBandwidth.Bw125kHz => 125_000,
            LoRaBandwidth.Bw250kHz => 250_000,
            LoRaBandwidth.Bw500kHz => 500_000,
            _ => throw new ArgumentOutOfRangeException(nameof(bandwidth)),
        };
    }
}
