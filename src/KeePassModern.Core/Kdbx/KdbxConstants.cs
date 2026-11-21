namespace KeePassModern.Core.Kdbx;

public static class KdbxConstants
{
    public const uint Signature1 = 0x9AA2D903;
    public const uint Signature2 = 0xB54BFB67;

    public const uint Version31 = 0x00030001;
    public const uint Version40 = 0x00040000;
    public const uint Version41 = 0x00040001;

    // Cipher UUIDs (16 bytes, big-endian byte arrays per KDBX spec)
    public static readonly byte[] CipherAes256 =
    [
        0x31, 0xC1, 0xF2, 0xE6, 0xBF, 0x71, 0x43, 0x50,
        0xBE, 0x58, 0x05, 0x21, 0x6A, 0xFC, 0x5A, 0xFF
    ];

    public static readonly byte[] CipherChaCha20 =
    [
        0xD6, 0x03, 0x8A, 0x2B, 0x8B, 0x6F, 0x4C, 0xB5,
        0xA5, 0x24, 0x33, 0x9A, 0x31, 0xDB, 0xB5, 0x9A
    ];

    public static readonly byte[] CipherTwofish =
    [
        0xAD, 0x68, 0xF2, 0x9F, 0x57, 0x6F, 0x4B, 0xB9,
        0xA3, 0x6A, 0xD4, 0x7A, 0xF9, 0x65, 0x34, 0x6C
    ];

    // KDF UUIDs
    public static readonly byte[] KdfAesKdbx3 =
    [
        0xC9, 0xD9, 0xF3, 0x9A, 0x62, 0x8A, 0x44, 0x60,
        0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F, 0xEA
    ];

    public static readonly byte[] KdfAesKdbx4 =
    [
        0x7C, 0x02, 0xBB, 0x82, 0x79, 0xA7, 0x4A, 0xC0,
        0x92, 0x7D, 0x11, 0x4A, 0x00, 0x64, 0x82, 0x38
    ];

    public static readonly byte[] KdfArgon2d =
    [
        0xEF, 0x63, 0x6D, 0xDF, 0x8C, 0x29, 0x44, 0x4B,
        0x91, 0xF7, 0xA9, 0xA4, 0x03, 0xE3, 0x0A, 0x0C
    ];

    public static readonly byte[] KdfArgon2id =
    [
        0x9E, 0x29, 0x8B, 0x19, 0x56, 0xDB, 0x47, 0x73,
        0xB2, 0x3D, 0xFC, 0x3E, 0xC6, 0xF0, 0xA1, 0xE6
    ];

    // Inner Salsa20 fixed IV per KDBX spec
    public static readonly byte[] InnerSalsa20Iv = [0xE8, 0x30, 0x09, 0x4B, 0x97, 0x20, 0x5D, 0x2A];

    // Standard KeePass string field keys
    public const string FieldTitle = "Title";
    public const string FieldUserName = "UserName";
    public const string FieldPassword = "Password";
    public const string FieldUrl = "URL";
    public const string FieldNotes = "Notes";
    public const string FieldOtp = "otp";
    public const string FieldTimeOtpSecretBase32 = "TimeOtp-Secret-Base32";
    public const string FieldTimeOtpLength = "TimeOtp-Length";
    public const string FieldTimeOtpPeriod = "TimeOtp-Period";
    public const string FieldTimeOtpAlgorithm = "TimeOtp-Algorithm";
}

public enum KdbxCipherAlgorithm
{
    Aes256,
    ChaCha20,
    Twofish
}

public enum KdbxKdfAlgorithm
{
    Argon2id,
    Argon2d,
    AesKdf
}

public enum KdbxCompression : uint
{
    None = 0,
    GZip = 1
}

public enum KdbxInnerStreamAlgorithm : uint
{
    None = 0,
    ArcFourVariant = 1,
    Salsa20 = 2,
    ChaCha20 = 3
}
