using System.Text;

namespace Kyvoq.Core.Services;

/// <summary>
/// 解析文本 KeyValues，并通过源文本位置修改值，保留未知字段、注释和排版。
/// </summary>
internal sealed class SteamVdfDocument
{
    private readonly string source;
    private readonly List<(int Start, int Length, string Text)> replacements = [];
    private readonly Dictionary<int, StringBuilder> insertions = [];
    private int position;

    public IReadOnlyList<Node> Roots { get; }

    /// <summary>
    /// 解析一个完整 VDF 文档，对不完整的语法拒绝修改。
    /// </summary>
    public SteamVdfDocument(string source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        Roots = ReadNodes(false, 0);
    }

    /// <summary>
    /// 按不区分大小写的名称查找唯一节点，拒绝有歧义的重复字段。
    /// </summary>
    public static Node? Find(IReadOnlyList<Node> nodes, string name)
    {
        var matches = nodes.Where(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException("Steam 配置中存在重复字段。")
        };
    }

    /// <summary>
    /// 修改已有标量字段，或在指定对象末尾添加字段。
    /// </summary>
    public void Set(Node parent, string name, string value, bool create = true)
    {
        if (parent.Children is null)
        {
            throw new InvalidDataException("Steam 配置对象结构不正确。");
        }

        var node = Find(parent.Children, name);
        if (node is not null)
        {
            if (node.Children is not null)
            {
                throw new InvalidDataException("Steam 配置字段不是文本值。");
            }

            replacements.Add((node.ValueStart, node.ValueLength, Quote(value)));
        }
        else if (create)
        {
            if (!insertions.TryGetValue(parent.ClosePosition, out var text))
            {
                text = new StringBuilder();
                insertions.Add(parent.ClosePosition, text);
            }

            var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            text.Append('\t').Append(Quote(name)).Append("\t\t").Append(Quote(value))
                .Append(newline).Append(' ', parent.Depth * 4);
        }
    }

    /// <summary>
    /// 删除一个完整节点，保留节点前后的空白和注释。
    /// </summary>
    public void Remove(Node node) => replacements.Add((node.Start, node.Length, string.Empty));

    /// <summary>
    /// 将局部变更应用到源文本，保留其余内容。
    /// </summary>
    public string Render()
    {
        var edits = replacements.Concat(insertions.Select(pair => (pair.Key, 0, pair.Value.ToString())));
        var result = new StringBuilder(source);
        foreach (var (start, length, text) in edits.OrderByDescending(edit => edit.Item1))
        {
            result.Remove(start, length).Insert(start, text);
        }

        return result.ToString();
    }

    /// <summary>
    /// 递归解析对象内容并限制异常嵌套深度。
    /// </summary>
    private List<Node> ReadNodes(bool nested, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("Steam 配置嵌套过深。");
        }

        var nodes = new List<Node>();
        while (true)
        {
            SkipTrivia();
            if (position == source.Length)
            {
                if (nested)
                {
                    throw new InvalidDataException("Steam 配置缺少结束括号。");
                }

                return nodes;
            }

            if (source[position] == '}')
            {
                if (!nested)
                {
                    throw new InvalidDataException("Steam 配置包含多余括号。");
                }

                return nodes;
            }

            var nodeStart = position;
            var name = ReadString();
            SkipTrivia();
            if (position < source.Length && source[position] == '{')
            {
                position++;
                var children = ReadNodes(true, depth + 1);
                nodes.Add(new Node(name, null, children, 0, 0, position, depth, nodeStart, position + 1 - nodeStart));
                position++;
            }
            else
            {
                var start = position;
                var value = ReadString();
                nodes.Add(new Node(name, value, null, start, position - start, 0, depth, nodeStart, position - nodeStart));
            }
        }
    }

    /// <summary>
    /// 读取带引号或无引号的文本值，并解码常用转义。
    /// </summary>
    private string ReadString()
    {
        SkipTrivia();
        if (position >= source.Length || source[position] is '{' or '}')
        {
            throw new InvalidDataException("Steam 配置缺少字段或值。");
        }

        if (source[position] != '"')
        {
            var start = position;
            while (position < source.Length && !char.IsWhiteSpace(source[position]) && source[position] is not '{' and not '}')
            {
                position++;
            }

            return source[start..position];
        }

        position++;
        var result = new StringBuilder();
        while (position < source.Length)
        {
            var character = source[position++];
            if (character == '"')
            {
                return result.ToString();
            }

            if (character == '\\' && position < source.Length)
            {
                character = source[position++];
                switch (character)
                {
                    case 'n': result.Append('\n'); break;
                    case 'r': result.Append('\r'); break;
                    case 't': result.Append('\t'); break;
                    case '\\': case '"': result.Append(character); break;
                    default: result.Append('\\').Append(character); break;
                }
            }
            else
            {
                result.Append(character);
            }
        }

        throw new InvalidDataException("Steam 配置包含未结束的字符串。");
    }

    /// <summary>
    /// 跳过空白、字节序标记和注释。
    /// </summary>
    private void SkipTrivia()
    {
        while (position < source.Length)
        {
            if (char.IsWhiteSpace(source[position]) || source[position] == '\uFEFF')
            {
                position++;
            }
            else if (source.AsSpan(position).StartsWith("//", StringComparison.Ordinal))
            {
                while (position < source.Length && source[position] != '\n')
                {
                    position++;
                }
            }
            else if (source.AsSpan(position).StartsWith("/*", StringComparison.Ordinal))
            {
                var end = source.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new InvalidDataException("Steam 配置包含未结束的注释。");
                }

                position = end + 2;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// 将值编码为合法的带引号 KeyValues 字符串。
    /// </summary>
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// 保存字段内容及其在源文本中的位置。
    /// </summary>
    internal sealed record Node(string Name, string? Value, IReadOnlyList<Node>? Children,
        int ValueStart, int ValueLength, int ClosePosition, int Depth, int Start, int Length);
}
