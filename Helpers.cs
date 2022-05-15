using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using Newtonsoft.Json;
using static System.Math;

using static Utils;

public static partial class Networking
{
    [StructLayout(LayoutKind.Sequential)]
    public struct VArray512
    {
        public const int SIZE = 512;
        int _datalen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=SIZE)]
        byte[] _data;
        public byte[] Data {get => (_data == null) ? null : _data.Take(_datalen).ToArray(); set
        {
            Assert(value.Length <= SIZE);
            if(_data == null || _data.Length != SIZE)
                _data = new byte[SIZE];
            System.Buffer.BlockCopy(value, 0, _data, 0, value.Length);
            _datalen = value.Length;
        }}
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct VArray256
    {
        public const int SIZE = 256;
        int _datalen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=SIZE)]
        byte[] _data;
        public byte[] Data {get => (_data == null) ? null : _data.Take(_datalen).ToArray(); set
        {
            Assert(value.Length <= SIZE);
            if(_data == null || _data.Length != SIZE)
                _data = new byte[SIZE];
            System.Buffer.BlockCopy(value, 0, _data, 0, value.Length);
            _datalen = value.Length;
        }}
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct VArray128
    {
        public const int SIZE = 128;
        int _datalen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=SIZE)]
        byte[] _data;
        public byte[] Data {get => (_data == null) ? null : _data.Take(_datalen).ToArray(); set
        {
            Assert(value.Length <= SIZE);
            if(_data == null || _data.Length != SIZE)
                _data = new byte[SIZE];
            System.Buffer.BlockCopy(value, 0, _data, 0, value.Length);
            _datalen = value.Length;
        }}
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct VArray64
    {
        public const int SIZE = 64;
        int _datalen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=SIZE)]
        byte[] _data;
        public byte[] Data {get => (_data == null) ? null : _data.Take(_datalen).ToArray(); set
        {
            Assert(value.Length <= SIZE);
            if(_data == null || _data.Length != SIZE)
                _data = new byte[SIZE];
            System.Buffer.BlockCopy(value, 0, _data, 0, value.Length);
            _datalen = value.Length;
        }}
    }
}