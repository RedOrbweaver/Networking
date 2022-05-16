/* NetworkErrors.cs

    Various exceptions and assertions

*/

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

    [System.Serializable]
    public class NetException : System.Exception
    {
        public NetException() { }
        public NetException(string message) : base(message) { }
        public NetException(string message, System.Exception inner) : base(message, inner) { }
        protected NetException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
    [System.Serializable]
    public class NetAssertionException : NetException
    {
        public NetAssertionException() { }
        public NetAssertionException(string message) : base(message) { }
        public NetAssertionException(string message, System.Exception inner) : base(message, inner) { }
        protected NetAssertionException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
    [System.Serializable]
    public class NetMessageMalformedException : NetException
    {
        public NetMessageMalformedException() { }
        public NetMessageMalformedException(string message) : base(message) { }
        public NetMessageMalformedException(string message, System.Exception inner) : base(message, inner) { }
        protected NetMessageMalformedException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
    [System.Serializable]
    public class NetUnknownMessageException : NetException
    {
        public NetUnknownMessageException() { }
        public NetUnknownMessageException(string message) : base(message) { }
        public NetUnknownMessageException(string message, System.Exception inner) : base(message, inner) { }
        protected NetUnknownMessageException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
    [System.Serializable]
    public class NetConnectionErrorException : NetException
    {
        public NetConnectionErrorException() { }
        public NetConnectionErrorException(string message) : base(message) { }
        public NetConnectionErrorException(string message, System.Exception inner) : base(message, inner) { }
        protected NetConnectionErrorException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
    [System.Serializable]
    public class NetConnectionTimeoutException : NetConnectionErrorException
    {
        public NetConnectionTimeoutException() { }
        public NetConnectionTimeoutException(string message) : base(message) { }
        public NetConnectionTimeoutException(string message, System.Exception inner) : base(message, inner) { }
        protected NetConnectionTimeoutException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
    public static void NetAssert(bool b, string msg = "An unspecified network assertion has been triggered")
    {
        if (!b)
            throw new NetAssertionException(msg);
    }

}
