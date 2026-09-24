namespace BeeMemoryBank.Core.Services;

/// <summary>
/// The node data key that encrypts the API's chat.db — the <c>'chat'</c> row of
/// <c>tbl_node_data_key</c>. A thin binding of <see cref="NodeDataKeyEnvelope"/> to that one name,
/// so the name is spelled once. The DEK-rotation rewrap does not use this class: it re-wraps every
/// node data key generically, without knowing what each one protects.
/// </summary>
public static class ChatDataKeyEnvelope
{
    public const string KeyName = "chat";

    public static byte[] Generate() => NodeDataKeyEnvelope.Generate();

    public static (byte[] wrapped, byte[] iv) Wrap(byte[] chatKey, byte[] masterDek)
        => NodeDataKeyEnvelope.Wrap(KeyName, chatKey, masterDek);

    public static byte[]? TryUnwrap(byte[] wrapped, byte[] iv, byte[] masterDek)
        => NodeDataKeyEnvelope.TryUnwrap(KeyName, wrapped, iv, masterDek);
}
