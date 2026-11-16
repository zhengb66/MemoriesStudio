namespace MemoriesStudio.Models;

/// <summary>下拉框里用的「值 + 中文显示」包装。</summary>
public sealed class Choice<T>
{
    public Choice(T value, string text)
    {
        Value = value;
        Text = text;
    }

    public T Value { get; }
    public string Text { get; }

    public override string ToString() => Text;
}
