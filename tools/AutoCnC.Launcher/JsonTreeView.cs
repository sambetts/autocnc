#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 or
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>A lazy, collapsible view over a JSON document.</summary>
	public sealed class JsonTreeView : UserControl
	{
		static readonly object Placeholder = new();
		readonly TreeView tree;

		internal string SourceText { get; private set; }
		internal string[] TopLevelLabels =>
			tree.Nodes.Count == 0 ? [] : tree.Nodes[0].Nodes.Cast<TreeNode>().Select(n => n.Text).ToArray();

		public JsonTreeView()
		{
			tree = new TreeView
			{
				Dock = DockStyle.Fill,
				BorderStyle = BorderStyle.None,
				BackColor = BattleWindow.Paper,
				ForeColor = BattleWindow.Ink,
				LineColor = BattleWindow.Rule,
				Font = new Font(FontFamily.GenericMonospace, 9f),
				ShowNodeToolTips = true,
				HideSelection = false
			};
			tree.BeforeExpand += (_, e) => Populate(e.Node);

			var collapse = new Button { Text = "Collapse all", AutoSize = true };
			collapse.Click += (_, _) => tree.CollapseAll();
			var expand = new Button { Text = "Expand selected", AutoSize = true };
			expand.Click += (_, _) => tree.SelectedNode?.Expand();
			var copy = new Button { Text = "Copy value", AutoSize = true };
			copy.Click += (_, _) =>
			{
				if (tree.SelectedNode?.Tag is JsonElement element)
					Clipboard.SetText(element.GetRawText());
			};

			var tools = new FlowLayoutPanel
			{
				Dock = DockStyle.Top,
				AutoSize = true,
				Padding = new Padding(4, 3, 4, 3),
				BackColor = Color.FromArgb(38, 38, 38)
			};
			tools.Controls.Add(collapse);
			tools.Controls.Add(expand);
			tools.Controls.Add(copy);

			Controls.Add(tree);
			Controls.Add(tools);
		}

		public void LoadJsonFile(string path, string missing)
		{
			tree.Nodes.Clear();
			if (!File.Exists(path))
			{
				SourceText = "";
				tree.Nodes.Add(new TreeNode(missing) { ForeColor = BattleWindow.Faded });
				return;
			}

			SourceText = File.ReadAllText(path);
			try
			{
				using var document = JsonDocument.Parse(SourceText);
				var rootElement = document.RootElement.Clone();
				var root = Node(Path.GetFileName(path), rootElement);
				tree.Nodes.Add(root);
				Populate(root);
				root.Expand();
			}
			catch (JsonException ex)
			{
				tree.Nodes.Add(new TreeNode($"Invalid JSON: {ex.Message}") { ForeColor = Color.Salmon });
			}
		}

		void Populate(TreeNode node)
		{
			if (node.Tag is not JsonElement element ||
				node.Nodes.Count != 1 ||
				!ReferenceEquals(node.Nodes[0].Tag, Placeholder))
				return;

			node.Nodes.Clear();
			if (element.ValueKind == JsonValueKind.Object)
			{
				foreach (var property in element.EnumerateObject())
					node.Nodes.Add(Node(property.Name, property.Value));
			}
			else if (element.ValueKind == JsonValueKind.Array)
			{
				var index = 0;
				foreach (var item in element.EnumerateArray())
					node.Nodes.Add(Node(ItemLabel(index++, item), item));
			}
		}

		static TreeNode Node(string name, JsonElement value)
		{
			var node = new TreeNode(Label(name, value))
			{
				Tag = value,
				ToolTipText = Scalar(value, truncate: false)
			};

			if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
				node.Nodes.Add(new TreeNode { Tag = Placeholder });

			return node;
		}

		static string ItemLabel(int index, JsonElement item)
		{
			if (item.ValueKind == JsonValueKind.Object &&
				item.TryGetProperty("id", out var id) &&
				id.ValueKind == JsonValueKind.String)
				return $"[{index}] {id.GetString()}";

			return $"[{index}]";
		}

		static string Label(string name, JsonElement value) => value.ValueKind switch
		{
			JsonValueKind.Object => $"{name} {{{value.EnumerateObject().Count()}}}",
			JsonValueKind.Array => $"{name} [{value.GetArrayLength()}]",
			_ => $"{name}: {Scalar(value, truncate: true)}"
		};

		static string Scalar(JsonElement value, bool truncate)
		{
			if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
				return "";

			var text = value.GetRawText();
			return truncate && text.Length > 160 ? text[..157] + "..." : text;
		}
	}
}
