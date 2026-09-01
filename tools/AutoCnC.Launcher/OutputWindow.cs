#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see LICENSE.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace AutoCnC.Launcher
{
	/// <summary>
	/// The output window: what your side experienced, and what the build had to say.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The battle tab is the one that matters, and it is not a match report. It is only the
	/// events the local player's code was in a position to react to — an enemy coming into view,
	/// a hit taken, a unit lost, a kill — recorded by the game through the same visibility rule
	/// <c>ctx.SenseThreats</c> filters with. Read against the graphs next door, that is the
	/// material for a better doctrine: the graph shows you the moment it went wrong, and this
	/// shows you what your code knew at that moment.
	/// </para>
	/// <para>
	/// Build output keeps its own tab rather than being interleaved. A doctrine that failed to
	/// compile and a doctrine that lost a fight are different problems, and mixing MSBuild
	/// diagnostics into a combat log helps with neither.
	/// </para>
	/// </remarks>
	public sealed class OutputWindow : BattleWindow
	{
		static readonly Color Spotted = Color.FromArgb(220, 190, 110);
		static readonly Color Attacked = Color.FromArgb(230, 140, 90);
		static readonly Color Lost = Color.FromArgb(225, 100, 100);
		static readonly Color Killed = Color.FromArgb(130, 200, 130);
		static readonly Color Built = Color.FromArgb(130, 175, 225);

		readonly BattleEventLog log;

		readonly TabControl tabs;
		readonly TabPage battleTab;
		readonly TabPage buildTab;
		readonly ListView events;
		readonly FlowLayoutPanel roster;
		readonly Label counts;
		readonly CheckBox follow;
		readonly TextBox build;

		int rendered;
		int rosterRows = -1;

		public OutputWindow(BattleEventLog log)
			: base("AutoC&C — Output")
		{
			this.log = log;

			events = BuildEventList();

			roster = new FlowLayoutPanel
			{
				Dock = DockStyle.Top,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				Padding = new Padding(6, 4, 6, 4),
				BackColor = Color.FromArgb(38, 38, 38)
			};

			battleTab = new TabPage("Battle") { BackColor = Paper, Padding = new Padding(2) };
			battleTab.Controls.Add(events);
			battleTab.Controls.Add(roster);
			battleTab.Controls.Add(BuildFooter(out counts, out follow));

			build = new TextBox
			{
				Dock = DockStyle.Fill,
				Multiline = true,
				ReadOnly = true,
				ScrollBars = ScrollBars.Vertical,
				WordWrap = false,
				BorderStyle = BorderStyle.None,
				BackColor = Paper,
				ForeColor = Ink,
				Font = new Font(FontFamily.GenericMonospace, 8.5f)
			};

			buildTab = new TabPage("Build output") { BackColor = Paper, Padding = new Padding(2) };
			buildTab.Controls.Add(build);

			tabs = new TabControl { Dock = DockStyle.Fill };
			tabs.TabPages.Add(battleTab);
			tabs.TabPages.Add(buildTab);

			Controls.Add(tabs);

			// Re-measured once the window is real: before the handle exists the font is whatever
			// the form was constructed with, not the one a scaled monitor will actually draw in.
			HandleCreated += (_, _) => SizeColumns();
			DpiChanged += (_, _) => SizeColumns();
		}

		void SizeColumns()
		{
			var specs = Columns;
			for (var i = 0; i < events.Columns.Count - 1 && i < specs.Length; i++)
				events.Columns[i].Width = ColumnWidth(events.Font, specs[i].Heading, specs[i].Widest);

			FillLastColumn(events);
		}

		ListView BuildEventList()
		{
			var list = new ListView
			{
				Dock = DockStyle.Fill,
				View = View.Details,
				FullRowSelect = true,
				HeaderStyle = ColumnHeaderStyle.Nonclickable,
				BorderStyle = BorderStyle.None,
				BackColor = Paper,
				ForeColor = Ink,
				OwnerDraw = true,
				MultiSelect = true
			};

			// Sized from the widest thing each column will hold rather than from a pixel count, so
			// the headings still read on a scaled display — which is where a fixed width clips.
			foreach (var (heading, widest) in Columns)
				list.Columns.Add(heading, ColumnWidth(list.Font, heading, widest));

			// Owner drawn throughout: a Details ListView otherwise paints its header in the system
			// light style regardless of BackColor, which next to a dark log looks like a bug.
			list.DrawColumnHeader += (_, e) =>
			{
				using var back = new SolidBrush(Color.FromArgb(45, 45, 45));
				using var rule = new Pen(Rule);

				e.Graphics.FillRectangle(back, e.Bounds);
				e.Graphics.DrawLine(rule, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

				TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, Rectangle.Inflate(e.Bounds, -6, 0), Faded,
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
			};

			list.DrawItem += (_, e) => e.DrawDefault = false;

			list.DrawSubItem += (_, e) =>
			{
				using var back = new SolidBrush(e.Item.Selected ? Color.FromArgb(58, 58, 66) : Paper);
				e.Graphics.FillRectangle(back, e.Bounds);

				TextRenderer.DrawText(e.Graphics, e.SubItem.Text, list.Font, Rectangle.Inflate(e.Bounds, -6, 0),
					e.SubItem.ForeColor,
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
			};

			// The last column takes whatever is left. Not only for the room: the strip past the
			// final column is the one part of the control we are never asked to draw, so leaving it
			// there means a band of system white down the side of a dark log.
			list.Resize += (_, _) => FillLastColumn(list);

			list.KeyDown += (_, e) =>
			{
				if (e.Control && e.KeyCode == Keys.C)
					CopySelection();
			};

			return list;
		}

		/// <summary>Each column, and the widest thing it is expected to hold.</summary>
		static (string Heading, string Widest)[] Columns =>
		[
			("Time", "000:00"),
			("Event", "attacked"),
			("Player", "Commander 00"),
			("Unit", "harv #99999"),
			("Other", "harv #99999 — Commander"),
			("Where", "(999,999)"),
			("Detail", "")
		];

		static int ColumnWidth(Font font, string heading, string widest)
		{
			var padding = TextRenderer.MeasureText("MM", font).Width;

			return padding + Math.Max(
				TextRenderer.MeasureText(heading, font).Width,
				TextRenderer.MeasureText(widest, font).Width);
		}

		static void FillLastColumn(ListView list)
		{
			var used = 0;
			for (var i = 0; i < list.Columns.Count - 1; i++)
				used += list.Columns[i].Width;

			list.Columns[^1].Width = Math.Max(160, list.ClientSize.Width - used);
		}

		Control BuildFooter(out Label countLabel, out CheckBox followBox)
		{
			countLabel = new Label { AutoSize = true, ForeColor = Faded, Margin = new Padding(3, 6, 12, 3), Text = "No events yet." };

			followBox = new CheckBox
			{
				Text = "Follow the battle",
				AutoSize = true,
				Checked = true,
				ForeColor = Ink,
				Margin = new Padding(3, 4, 12, 3)
			};

			var copy = new LinkLabel { Text = "Copy", AutoSize = true, Margin = new Padding(3, 6, 12, 3) };
			copy.LinkClicked += (_, _) => CopySelection();

			var open = new LinkLabel { Text = "Open the log file", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
			open.LinkClicked += (_, _) => OpenLogFile();

			foreach (var link in new[] { copy, open })
			{
				link.LinkColor = Color.FromArgb(130, 175, 225);
				link.ActiveLinkColor = Color.White;
			}

			var footer = new FlowLayoutPanel
			{
				Dock = DockStyle.Bottom,
				AutoSize = true,
				AutoSizeMode = AutoSizeMode.GrowAndShrink,
				Padding = new Padding(6, 2, 6, 2),
				BackColor = Color.FromArgb(38, 38, 38)
			};

			footer.Controls.Add(countLabel);
			footer.Controls.Add(followBox);
			footer.Controls.Add(copy);
			footer.Controls.Add(open);
			return footer;
		}

		// -------------------------------------------------------------------
		// Build output
		// -------------------------------------------------------------------
		public void ClearBuildOutput()
		{
			build.Clear();
			tabs.SelectedTab = buildTab;
		}

		public void AppendBuildOutput(string line)
		{
			build.AppendText(line + Environment.NewLine);
		}

		/// <summary>Clears the battle tab, ready for a new match to fill it in.</summary>
		public void ClearBattle()
		{
			events.Items.Clear();
			roster.Controls.Clear();
			rendered = 0;
			rosterRows = -1;
			counts.Text = "Waiting for the battle to start…";
		}

		// -------------------------------------------------------------------
		// The battle
		// -------------------------------------------------------------------

		/// <summary>Draws whatever the log has gained since the last call.</summary>
		public void ShowBattle(bool finished)
		{
			if (log.Restarted)
				ClearBattle();

			ShowRoster();

			var pending = log.Events.Count - rendered;
			if (pending > 0)
			{
				events.BeginUpdate();

				foreach (var recorded in log.Events.Skip(rendered))
					events.Items.Add(Row(recorded));

				rendered = log.Events.Count;

				if (follow.Checked && events.Items.Count > 0)
					events.EnsureVisible(events.Items.Count - 1);

				events.EndUpdate();

				// The battle is the reason this window is open, so the first event brings it
				// forward. Only the first: pulling the tab out from under somebody reading the
				// build output every half second would be its own kind of rude.
				if (rendered == pending)
					tabs.SelectedTab = battleTab;
			}

			counts.Text = Tally(finished);
			Text = finished ? "AutoC&C — Output (battle finished)" : "AutoC&C — Output";
		}

		string Tally(bool finished)
		{
			if (log.Events.Count == 0)
				return finished
					? "Nothing was recorded. Either no battle started, or it ended before anything happened."
					: "Waiting for the battle to start…";

			var by = log.Events.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());

			string Part(string kind, string label) =>
				by.TryGetValue(kind, out var n) ? $"{n} {label}" : null;

			var parts = new[]
			{
				Part("spotted", "sightings"),
				Part("attacked", "hits taken"),
				Part("lost", "lost"),
				Part("killed", "killed"),
				Part("built", "built")
			}.Where(p => p != null);

			return $"{Csv.Clock(log.Events[^1].Seconds)} — {string.Join(", ", parts)}.";
		}

		/// <summary>
		/// Lists everybody in the match, in the colours the game gave them, so the rows below can
		/// be read without working out who "HAL 9001 2" is.
		/// </summary>
		void ShowRoster()
		{
			if (log.Sides.Count == rosterRows)
				return;

			rosterRows = log.Sides.Count;
			roster.Controls.Clear();

			if (rosterRows == 0)
				return;

			roster.Controls.Add(new Label
			{
				Text = "Playing:",
				AutoSize = true,
				ForeColor = Faded,
				Margin = new Padding(3, 3, 8, 3)
			});

			foreach (var side in log.Sides)
				roster.Controls.Add(new Label
				{
					Text = side.Label,
					AutoSize = true,
					ForeColor = side.Colour,
					Font = side.IsYou ? new Font(Font, FontStyle.Bold) : Font,
					Margin = new Padding(3, 3, 14, 3)
				});
		}

		ListViewItem Row(BattleEvent recorded)
		{
			var mine = log.ColourOf(recorded.Player);
			var theirs = log.ColourOf(recorded.OtherPlayer);

			var unit = BattleEvent.Name(recorded.Actor, recorded.ActorId);
			var otherUnit = BattleEvent.Name(recorded.OtherActor, recorded.OtherActorId);

			var other = otherUnit.Length > 0 && recorded.OtherPlayer.Length > 0
				? $"{otherUnit} — {recorded.OtherPlayer}"
				: otherUnit.Length > 0 ? otherUnit : recorded.OtherPlayer;

			var item = new ListViewItem(Csv.Clock(recorded.Seconds)) { UseItemStyleForSubItems = false };
			item.SubItems[0].ForeColor = Faded;

			void Cell(string text, Color colour) => item.SubItems.Add(text, colour, Color.Transparent, Font);

			Cell(recorded.Kind, KindColour(recorded.Kind));
			Cell(recorded.Player, mine);
			Cell(unit, mine);
			Cell(other, theirs);
			Cell(recorded.Where, Faded);
			Cell(recorded.Detail, Faded);

			return item;
		}

		static Color KindColour(string kind) => kind switch
		{
			"spotted" => Spotted,
			"attacked" => Attacked,
			"lost" => Lost,
			"killed" => Killed,
			"built" => Built,
			_ => Color.White
		};

		/// <summary>
		/// Copies the selected rows, or all of them, as tab-separated text.
		/// </summary>
		/// <remarks>
		/// Tabs rather than the file's own commas: what this is for is dropping a fight into a
		/// spreadsheet or a message, and both of those paste columns from tabs.
		/// </remarks>
		void CopySelection()
		{
			var rows = events.SelectedItems.Count > 0
				? events.SelectedItems.Cast<ListViewItem>()
				: events.Items.Cast<ListViewItem>();

			var text = new StringBuilder();
			foreach (var row in rows)
				text.AppendLine(string.Join('\t', row.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)));

			if (text.Length > 0)
				Clipboard.SetText(text.ToString());
		}

		void OpenLogFile()
		{
			if (string.IsNullOrEmpty(log.Path) || !File.Exists(log.Path))
			{
				MessageBox.Show(this, "There is no battle log yet — launch a battle first.", "AutoC&C",
					MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}

			// Revealed rather than opened: it is a CSV, and whatever is registered for those is
			// unlikely to be the thing somebody wants right now.
			Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{log.Path}\"") { UseShellExecute = true });
		}
	}
}
