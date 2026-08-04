using System;
using System.Globalization;

namespace QQReborn.App.Services
{
    /// <summary>
    /// Pure host/port parsing for gateway URLs (no UWP dependency). Unit-testable.
    /// </summary>
    public static class GatewayEndpoint
    {
        public const int DefaultPort = 8765;
        public const string DefaultHost = "localhost";

        /// <summary>
        /// Accepts bare host, host:port, [ipv6]:port, ws(s)://host[:port][/path], http(s)://...
        /// Returns host only; updates <paramref name="port"/> when the input embeds one.
        /// </summary>
        public static string NormalizeServerHost(string raw, ref int port)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DefaultHost;
            var s = raw.Trim();

            if (s.StartsWith("[", StringComparison.Ordinal) && s.EndsWith("]", StringComparison.Ordinal) && s.Length > 2)
                return s.Substring(1, s.Length - 2);

            var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0)
                s = s.Substring(schemeIdx + 3);

            var cut = s.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0) s = s.Substring(0, cut);
            s = s.Trim();
            if (string.IsNullOrEmpty(s)) return DefaultHost;

            if (s.StartsWith("[", StringComparison.Ordinal))
            {
                var close = s.IndexOf(']');
                if (close > 1)
                {
                    var hostPart = s.Substring(1, close - 1);
                    if (close + 1 < s.Length && s[close + 1] == ':')
                    {
                        var portPart = s.Substring(close + 2);
                        int embedded;
                        if (int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out embedded)
                            && embedded > 0 && embedded < 65536)
                            port = embedded;
                    }
                    return string.IsNullOrWhiteSpace(hostPart) ? DefaultHost : hostPart;
                }
            }

            var lastColon = s.LastIndexOf(':');
            if (lastColon > 0 && s.IndexOf(':') == lastColon)
            {
                var hostPart = s.Substring(0, lastColon).Trim();
                var portPart = s.Substring(lastColon + 1).Trim();
                int embedded;
                if (int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out embedded)
                    && embedded > 0 && embedded < 65536)
                {
                    port = embedded;
                    return string.IsNullOrWhiteSpace(hostPart) ? DefaultHost : hostPart;
                }
            }

            return s;
        }

        public static string BuildWsUrl(string host, int port)
        {
            if (port <= 0 || port >= 65536) port = DefaultPort;
            host = NormalizeServerHost(host ?? "", ref port);
            return "ws://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/ws";
        }
    }
}
