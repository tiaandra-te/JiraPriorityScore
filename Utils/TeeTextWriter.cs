using System.Text;

namespace JiraPriorityScore.Utils;

public sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _primary;
    private readonly StringBuilder _buffer;

    public TeeTextWriter(TextWriter primary, StringBuilder buffer)
    {
        _primary = primary;
        _buffer = buffer;
    }

    public override Encoding Encoding => _primary.Encoding;

    public override void Write(char value)
    {
        _primary.Write(value);
        _buffer.Append(value);
    }

    public override void Write(string? value)
    {
        _primary.Write(value);
        _buffer.Append(value);
    }

    public override void WriteLine(string? value)
    {
        _primary.WriteLine(value);
        _buffer.AppendLine(value);
    }
}
