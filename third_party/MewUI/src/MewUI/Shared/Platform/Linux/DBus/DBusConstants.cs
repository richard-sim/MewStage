namespace Aprillz.MewUI.Platform.Linux.DBus;

internal static class DBusConstants
{
    // Message types
    internal const byte MethodCall = 1;
    internal const byte MethodReturn = 2;
    internal const byte Error = 3;
    internal const byte Signal = 4;

    // Header field codes
    internal const byte FieldPath = 1;
    internal const byte FieldInterface = 2;
    internal const byte FieldMember = 3;
    internal const byte FieldErrorName = 4;
    internal const byte FieldReplySerial = 5;
    internal const byte FieldDestination = 6;
    internal const byte FieldSender = 7;
    internal const byte FieldSignature = 8;

    // Well-known names
    internal const string BusName = "org.freedesktop.DBus";
    internal const string BusPath = "/org/freedesktop/DBus";
    internal const string BusInterface = "org.freedesktop.DBus";

    // Endianness
    internal const byte LittleEndian = (byte)'l';
    internal const byte BigEndian = (byte)'B';

    // Protocol version
    internal const byte ProtocolVersion = 1;

    // Type signatures
    internal const byte SigByte = (byte)'y';
    internal const byte SigBool = (byte)'b';
    internal const byte SigInt16 = (byte)'n';
    internal const byte SigUInt16 = (byte)'q';
    internal const byte SigInt32 = (byte)'i';
    internal const byte SigUInt32 = (byte)'u';
    internal const byte SigInt64 = (byte)'x';
    internal const byte SigUInt64 = (byte)'t';
    internal const byte SigString = (byte)'s';
    internal const byte SigObjectPath = (byte)'o';
    internal const byte SigSignature = (byte)'g';
    internal const byte SigVariant = (byte)'v';
    internal const byte SigArray = (byte)'a';
    internal const byte SigStructBegin = (byte)'(';
    internal const byte SigStructEnd = (byte)')';
}
