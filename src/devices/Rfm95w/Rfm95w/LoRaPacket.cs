namespace Iot.Device.Rfm95w
{
    /// <summary>
    /// A received LoRa packet.
    /// </summary>
    /// <param name="Payload">Payload bytes as transmitted.</param>
    /// <param name="Rssi">Packet RSSI in dBm.</param>
    /// <param name="Snr">Packet signal-to-noise ratio in dB (negative means below the noise floor, which LoRa tolerates).</param>
    /// <param name="Timestamp">Monotonic timestamp from <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> when the packet was read.</param>
    public sealed record LoRaPacket(byte[] Payload, double Rssi, double Snr, long Timestamp)
    {
        /// <inheritdoc />
        public override string ToString() => $"{Payload.Length} bytes, RSSI {Rssi:F0} dBm, SNR {Snr:F2} dB";
    }
}
