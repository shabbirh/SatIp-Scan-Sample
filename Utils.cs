﻿/*  
    Copyright (C) <2007-2017>  <Kay Diefenthal>

    SatIp is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    SatIp is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with SatIp.  If not, see <http://www.gnu.org/licenses/>.
*/
using System;
using System.Text;

namespace SatIp
{
    public class Utils
    {
        //public static List<Service> GetStationsFromLocalFile_m3u(string fileName)
        //{
        //    using (StreamReader reader = File.OpenText(fileName))
        //    {
        //        string[] strArray = reader.ReadToEnd().Split(new[] { "\n", "\r", "\n\r", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        //        var list = new List<Service>();
        //        if (strArray[0].Trim().ToUpper() == "#EXTM3U")
        //        {
        //            var name = string.Empty;
        //            var parameters = new string[15];
        //            for (int i = 0; i < strArray.Length; i++)
        //            {
        //                if (strArray[i].StartsWith("#EXTINF", StringComparison.CurrentCultureIgnoreCase))
        //                {
        //                    var strArray2 = strArray[i].Split(new[] { ":", "," }, StringSplitOptions.None);
        //                    if (strArray2.Length > 2)
        //                    {
        //                        name = strArray2[2];
        //                        parameters = strArray[++i].Split('&');
        //                    }
        //                    list.Add(new Service(name, parameters));
        //                }
        //                else if (strArray[i].StartsWith("# ", StringComparison.CurrentCultureIgnoreCase))
        //                {
        //                    name = strArray[i].Substring(2);
        //                }
        //            }
        //        }
        //        return list;
        //    }
        //}
        public static int Convert2BytesToInt(byte[] buffer, int offset)
        {
            int temp = (int)buffer[offset];
            temp = (temp * 256) + buffer[offset + 1];

            return (temp);
        }
        public static int Convert3BytesToInt(byte[] buffer, int offset)
        {
            int temp = (int)buffer[offset];
            temp = (temp * 256) + buffer[offset + 1];
            temp = (temp * 256) + buffer[offset + 2];

            return (temp);
        }
        public static int Convert4BytesToInt(byte[] buffer, int offset)
        {
            int temp =(int)buffer[offset]; 
            temp = (temp * 256) + buffer[offset + 1];
            temp = (temp * 256) + buffer[offset + 2];
            temp = (temp * 256) + buffer[offset + 3];
            
            return (temp);
        }
        public static long Convert4BytesToLong(byte[] buffer, int offset)
        {
            long temp = 0;

            for (int index = 0; index < 4; index++)
                temp = (temp * 256) + buffer[offset + index];

            return (temp);
        }
        public static long Convert8BytesToLong(byte[] buffer, int offset)
        {
            long temp = 0;

            for (int index = 0; index < 8; index++)
                temp = (temp * 256) + buffer[offset + index];

            return (temp);
        }
        public static string ConvertBytesToString(byte[] buffer, int offset, int length)
        {
            StringBuilder reply = new StringBuilder(4);
            for (int index = 0; index < length; index++)
                reply.Append((char)buffer[offset + index]);
            return (reply.ToString());
        }
        public static DateTime NptTimestampToDateTime(long nptTimestamp) { return NptTimestampToDateTime((uint)((nptTimestamp >> 32) & 0xFFFFFFFF), (uint)(nptTimestamp & 0xFFFFFFFF),null); }
        public static DateTime NptTimestampToDateTime(uint seconds, uint fractions, DateTime? epoch )
        {
            ulong ticks =(ulong)((seconds * TimeSpan.TicksPerSecond) + ((fractions * TimeSpan.TicksPerSecond) / 0x100000000L));
            if (epoch.HasValue) return epoch.Value + TimeSpan.FromTicks((Int64)ticks);
            return (seconds & 0x80000000L) == 0 ? UtcEpoch2036 + TimeSpan.FromTicks((Int64)ticks) : UtcEpoch1900 + TimeSpan.FromTicks((Int64)ticks);
        }

        //When the First Epoch will wrap (The real Y2k)
        public static DateTime UtcEpoch2036 = new DateTime(2036, 2, 7, 6, 28, 16, DateTimeKind.Utc);

        public static DateTime UtcEpoch1900 = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static DateTime UtcEpoch1970 = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    }
}
