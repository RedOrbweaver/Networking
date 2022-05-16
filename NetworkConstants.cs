/* NetworkingConstants.cs

    Networking constants

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
    public const int MSG_INDEX_IGNORE = 0;
    public const int N_SAVED_MESSAGES = 128;
    public const int PASSWORD_LENGTH = 32;
    public const int ID_RELAY = 0;
    public const int ID_NULL = -1;
    public const int ID_ALL = -2;
    public const int ID_UNKNOWN = -3;

    public const int MESSAGE_MAX_LEN = 128-sizeof(ulong);
    public const int DEFAULT_TIMEOUT_MS = 1000;
    public const int DEFAULT_RETRIES = 3;
    public const int DEFAULT_RETRY_DELAY_MS = 250;
}
