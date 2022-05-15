using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

using Newtonsoft.Json;
using static System.Math;

using static Utils;

public static partial class Networking
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PublicKey
    {
        
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PrivateKey
    {
        
    }
    public static byte[] Encrypt(byte[] data, PublicKey key)
    {
        return data.ToArray();
    }
    public static byte[] Decrypt(byte[] data, PrivateKey key)
    {
        return data.ToArray();
    }
    public static (PrivateKey privkey, PublicKey pubkey) GenerateKeyPair()
    {
        return (new PrivateKey(), new PublicKey());
    }
}