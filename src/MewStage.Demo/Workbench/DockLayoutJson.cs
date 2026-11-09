using System.Globalization;

namespace MewStage.Demo.Workbench;

/// <summary>Writes FlexLayout JSON (MewDock's layout format) from a small tree. A row's children alternate orientation: the root row is horizontal, a nested row vertical.</summary>
internal static class DockLayoutJson
{
    /// <summary>A layout node.</summary>
    internal abstract class Node
    {
        internal abstract void Write(System.Text.StringBuilder sb);
    }

    /// <summary>A row (or, nested, a column) of children with relative weights.</summary>
    public static Node Row(double weight, params Node[] children)
    {
        return new RowNode(weight, children);
    }

    /// <summary>A tool tabset (caption bar, not closable).</summary>
    public static Node Tools(double weight, params (string Name, string Component)[] tabs)
    {
        return new TabSetNode(weight, false, tabs);
    }

    /// <summary>The document tabset.</summary>
    public static Node Document(double weight, params (string Name, string Component)[] tabs)
    {
        return new TabSetNode(weight, true, tabs);
    }

    /// <summary>The layout JSON for a root row.</summary>
    public static string Root(params Node[] rootChildren)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"global\":{},\"borders\":[],\"layout\":");
        new RowNode(100, rootChildren).Write(sb);
        sb.Append('}');
        return sb.ToString();
    }

    private sealed class RowNode : Node
    {
        private readonly double _weight;
        private readonly Node[] _children;

        public RowNode(double weight, Node[] children)
        {
            _weight = weight;
            _children = children;
        }

        internal override void Write(System.Text.StringBuilder sb)
        {
            sb.Append("{\"type\":\"row\",\"weight\":").Append(_weight.ToString(CultureInfo.InvariantCulture)).Append(",\"children\":[");
            for (int i = 0; i < _children.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                _children[i].Write(sb);
            }

            sb.Append("]}");
        }
    }

    private sealed class TabSetNode : Node
    {
        private readonly double _weight;
        private readonly bool _isDocument;
        private readonly (string Name, string Component)[] _tabs;

        public TabSetNode(double weight, bool isDocument, (string, string)[] tabs)
        {
            _weight = weight;
            _isDocument = isDocument;
            _tabs = tabs;
        }

        internal override void Write(System.Text.StringBuilder sb)
        {
            sb.Append("{\"type\":\"tabset\",\"weight\":").Append(_weight.ToString(CultureInfo.InvariantCulture)).Append(",\"children\":[");
            for (int i = 0; i < _tabs.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"type\":\"tab\",\"name\":\"").Append(_tabs[i].Name).Append("\",\"component\":\"").Append(_tabs[i].Component).Append('"');
                if (!_isDocument)
                {
                    sb.Append(",\"isDocument\":false");
                }

                sb.Append(",\"enableClose\":false}");
            }

            sb.Append("]}");
        }
    }
}
