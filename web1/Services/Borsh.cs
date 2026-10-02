using System.Text;

namespace PolymarketApp.Services;

public class BorshWriter
{
    readonly List<byte> _b = new();
    public BorshWriter Raw(byte[] d) { _b.AddRange(d); return this; }
    public BorshWriter U64(ulong v) { _b.AddRange(BitConverter.GetBytes(v)); return this; }
    public BorshWriter I64(long v) { _b.AddRange(BitConverter.GetBytes(v)); return this; }
    public BorshWriter Str(string s)
    {
        var d = Encoding.UTF8.GetBytes(s);
        _b.AddRange(BitConverter.GetBytes((uint)d.Length));
        _b.AddRange(d);
        return this;
    }
    public byte[] ToArray() => _b.ToArray();
    
    public BorshWriter U8(byte v) { _b.Add(v); return this; }
}