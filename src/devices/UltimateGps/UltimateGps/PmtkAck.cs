using System.Globalization;

namespace Iot.Device.UltimateGps
{
    /// <summary>
    /// Result flag of a PMTK001 acknowledgement.
    /// </summary>
    public enum PmtkAckFlag
    {
        /// <summary>Invalid command or packet.</summary>
        Invalid = 0,

        /// <summary>Unsupported command or packet type.</summary>
        Unsupported = 1,

        /// <summary>Valid command but action failed.</summary>
        Failed = 2,

        /// <summary>Valid command and action succeeded.</summary>
        Success = 3,
    }

    /// <summary>
    /// A PMTK001 acknowledgement: "$PMTK001,&lt;command type&gt;,&lt;flag&gt;*CS".
    /// </summary>
    public sealed record PmtkAck(int CommandType, PmtkAckFlag Flag)
    {
        /// <summary>True when the module reports success.</summary>
        public bool IsSuccess => Flag == PmtkAckFlag.Success;

        /// <summary>
        /// Parses an acknowledgement line. Accepts with or without checksum and line terminator; verifies the
        /// checksum when present.
        /// </summary>
        public static bool TryParse(string? line, out PmtkAck? ack)
        {
            ack = null;
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }

            line = line.Trim();
            if (!line.StartsWith("$PMTK001,", StringComparison.Ordinal))
            {
                return false;
            }

            string body = line.Substring(1);
            int star = body.IndexOf('*');
            if (star >= 0)
            {
                string checksumText = body.Substring(star + 1);
                body = body.Substring(0, star);
                if (!byte.TryParse(checksumText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte expected) ||
                    expected != PmtkCommand.Checksum(body))
                {
                    return false;
                }
            }

            string[] fields = body.Split(',');
            if (fields.Length < 3 ||
                !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type) ||
                !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int flag) ||
                flag < 0 || flag > 3)
            {
                return false;
            }

            ack = new PmtkAck(type, (PmtkAckFlag)flag);
            return true;
        }
    }
}
