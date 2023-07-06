//********************************************************* 
// 
//    Copyright (c) Microsoft. All rights reserved. 
//    This code is licensed under the Microsoft Public License. 
//    THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF 
//    ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY 
//    IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR 
//    PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT. 
// 
//********************************************************* 

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Azure.StreamAnalytics;
using Microsoft.Azure.StreamAnalytics.Serialization;
using ProtobufDeserializer.Devices;
using ProtobufDeserializer.Model;

namespace ProtobufDeserializer;

public class MessageBodyDeserializer : StreamDeserializer<Data>
{
    public override IEnumerable<Data> Deserialize(Stream stream)
    {
        string reader = new StreamReader(stream, Encoding.UTF32, false).ReadToEndAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        string HexData = ConvertToHex(reader);
        var text = Regex.Replace(HexData, @"\t|\n|\r|\s", "");
        try
        {
            var bytes = StringToBytes(text);
            var data = DecodeTcpData(bytes);
            return new List<Data> { data };

        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid data received.", ex.Message);
        }

    }

    public override void Initialize(StreamingContext streamingContext)
    {
    }
    private static TcpDataPacket DecodeTcpPacket(byte[] request)
    {
        var reader = new ReverseBinaryReader(new MemoryStream(request));
        var decoder = new DataDecoder(reader);
        return decoder.DecodeTcpData();
    }
    private static byte[] StringToBytes(string data)
    {
        var array = new byte[data.Length / 2];

        var substring = 0;
        for (var i = 0; i < array.Length; i++)
        {
            array[i] = Byte.Parse(data.Substring(substring, 2), NumberStyles.AllowHexSpecifier);
            substring += 2;
        }

        return array;
    }
    private static Data DecodeTcpData(byte[] bytes)
    {
        var parser = new DataReader(bytes);

        var dataList = new[]
        {
                parser.ReadData(4, DataType.Preamble),
                parser.ReadData(4, DataType.AvlDataArrayLength),
                DecodeAvlData(parser),
                parser.ReadData(4, DataType.Crc)
            };

        var result = new Data("TCP AVL Data Packet")
        {
            SubItems = dataList
        };

        return result;
    }
    private static Data DecodeAvlData(DataReader parser)
    {
        var codecIdData = parser.ReadData(1, DataType.CodecId);
        var countData = parser.ReadData(1, DataType.AvlDataCount);

        var avlDataList = new List<Data> { codecIdData, countData };
        var result = new Data("Data");

        for (var a = 0; a < Int32.Parse(countData.Value); a++)
        {
            var items = new PacketDecoder(parser, codecIdData.Value);

            // Fake data to show nodes in tree
            var startArraySegment = items.AvlItems.First().ArraySegment;
            var avlData = new Data("AVL Data")
            {
                ArraySegment =
                    new ArraySegment<byte>(startArraySegment.Array, startArraySegment.Offset,
                        items.IoElementSubItems.Last().ArraySegment.Offset - startArraySegment.Offset + 1)
            };

            var gpsElement = new Data("GPS Element")
            {
                ArraySegment =
                    new ArraySegment<byte>(startArraySegment.Array, items.GpsElementSubItems.First().ArraySegment.Offset, 15)
            };

            var ioElement = new Data("I/O Element")
            {
                ArraySegment =
                    new ArraySegment<byte>(startArraySegment.Array, items.IoElementSubItems.First().ArraySegment.Offset,
                        items.IoElementSubItems.Last().ArraySegment.Offset - items.IoElementSubItems.First().ArraySegment.Offset + 1)
            };

            gpsElement.SubItems = items.GpsElementSubItems.ToArray();
            ioElement.SubItems = items.IoElementSubItems.ToArray();

            var avlDataSubItems = new List<Data>();
            avlDataSubItems.AddRange(items.AvlItems);
            avlDataSubItems.Add(gpsElement);
            avlDataSubItems.Add(ioElement);

            avlData.SubItems = avlDataSubItems.ToArray();
            avlDataList.Add(avlData);
        }
        avlDataList.Add(parser.ReadData(1, DataType.AvlDataCount));
        result.SubItems = avlDataList.ToArray();
        result.ArraySegment = new ArraySegment<byte>(codecIdData.ArraySegment.Array, codecIdData.ArraySegment.Offset,
            avlDataList.Last().ArraySegment.Offset - codecIdData.ArraySegment.Offset + 1);

        return result;
    }
    private static string ConvertToHex(string corruptedData)
    {
        StringBuilder hexBuilder = new StringBuilder();

        for (int i = 0; i < corruptedData.Length; i++)
        {
            char currentChar = corruptedData[i];

            // Skip non-hex characters
            if (currentChar == '\\' && i + 1 < corruptedData.Length && corruptedData[i + 1] == 'u')
            {
                // Extract the hex value
                string hexValue = corruptedData.Substring(i + 2, 4);

                // Convert the hex value to a Unicode character
                char unicodeChar = (char)Convert.ToInt32(hexValue, 16);

                // Append the character as a hex string
                hexBuilder.Append(BitConverter.ToString(BitConverter.GetBytes(unicodeChar)).Replace("-", ""));

                // Skip the processed characters
                i += 5;
            }
            else
            {
                // Append the non-hex character as a hex string
                hexBuilder.Append(BitConverter.ToString(Encoding.Default.GetBytes(currentChar.ToString())).Replace("-", ""));
            }
        }

        return hexBuilder.ToString();
    }

}
