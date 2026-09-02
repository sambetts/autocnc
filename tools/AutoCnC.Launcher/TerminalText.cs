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
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

namespace AutoCnC.Launcher
{
	public readonly record struct TerminalStyle(Color? Foreground, bool Bold);
	public readonly record struct TerminalSpan(string Text, TerminalStyle Style);

	public sealed class TerminalLine
	{
		public string PlainText { get; }
		public IReadOnlyList<TerminalSpan> Spans { get; }

		public TerminalLine(string plainText, IReadOnlyList<TerminalSpan> spans)
		{
			PlainText = plainText;
			Spans = spans;
		}

		public static TerminalLine Plain(string text) =>
			new(text ?? "", [new TerminalSpan(text ?? "", default)]);
	}

	/// <summary>Converts an ANSI terminal stream into stable text and colored spans.</summary>
	public sealed class TerminalTextParser
	{
		static readonly Color[] StandardColors =
		[
			Color.FromArgb(12, 12, 12),
			Color.FromArgb(197, 15, 31),
			Color.FromArgb(19, 161, 14),
			Color.FromArgb(193, 156, 0),
			Color.FromArgb(0, 55, 218),
			Color.FromArgb(136, 23, 152),
			Color.FromArgb(58, 150, 221),
			Color.FromArgb(204, 204, 204)
		];

		static readonly Color[] BrightColors =
		[
			Color.FromArgb(118, 118, 118),
			Color.FromArgb(231, 72, 86),
			Color.FromArgb(22, 198, 12),
			Color.FromArgb(249, 241, 165),
			Color.FromArgb(59, 120, 255),
			Color.FromArgb(180, 0, 158),
			Color.FromArgb(97, 214, 214),
			Color.FromArgb(242, 242, 242)
		];

		TerminalStyle style;

		public void Reset() => style = default;

		public TerminalLine ParseLine(string raw)
		{
			raw ??= "";
			var spans = new List<TerminalSpan>();
			var text = new StringBuilder();

			void Flush()
			{
				if (text.Length == 0)
					return;

				spans.Add(new TerminalSpan(text.ToString(), style));
				text.Clear();
			}

			for (var i = 0; i < raw.Length;)
			{
				if (raw[i] == '\x1B' && i + 1 < raw.Length && raw[i + 1] == '[')
				{
					var final = FindControlEnd(raw, i + 2);
					if (final < 0)
						break;

					Flush();
					if (raw[final] == 'm')
						ApplySgr(raw[(i + 2)..final]);
					i = final + 1;
					continue;
				}

				if (raw[i] == '\u009B')
				{
					var final = FindControlEnd(raw, i + 1);
					if (final < 0)
						break;

					Flush();
					if (raw[final] == 'm')
						ApplySgr(raw[(i + 1)..final]);
					i = final + 1;
					continue;
				}

				if (raw[i] == '\x1B' && i + 1 < raw.Length && raw[i + 1] == ']')
				{
					var final = FindOscEnd(raw, i + 2);
					i = final < 0 ? raw.Length : final;
					continue;
				}

				if (raw[i] == '\b')
				{
					if (text.Length > 0)
						text.Length--;
					i++;
					continue;
				}

				if (raw[i] == '\r' || raw[i] != '\t' && char.IsControl(raw[i]))
				{
					i++;
					continue;
				}

				text.Append(raw[i++]);
			}

			Flush();
			return new TerminalLine(string.Concat(spans.Select(s => s.Text)), spans);
		}

		void ApplySgr(string parameters)
		{
			var codes = parameters.Length == 0
				? [0]
				: parameters.Split(';').Select(p => int.TryParse(p, out var value) ? value : 0).ToArray();

			for (var i = 0; i < codes.Length; i++)
			{
				var code = codes[i];
				if (code == 0)
					style = default;
				else if (code == 1)
					style = style with { Bold = true };
				else if (code == 22)
					style = style with { Bold = false };
				else if (code == 39)
					style = style with { Foreground = null };
				else if (code is >= 30 and <= 37)
					style = style with { Foreground = StandardColors[code - 30] };
				else if (code is >= 90 and <= 97)
					style = style with { Foreground = BrightColors[code - 90] };
				else if (code is 38 or 48)
				{
					var foreground = code == 38;
					if (i + 2 < codes.Length && codes[i + 1] == 5)
					{
						if (foreground)
							style = style with { Foreground = Color256(codes[i + 2]) };
						i += 2;
					}
					else if (i + 4 < codes.Length && codes[i + 1] == 2)
					{
						if (foreground)
							style = style with
							{
								Foreground = Color.FromArgb(
									Math.Clamp(codes[i + 2], 0, 255),
									Math.Clamp(codes[i + 3], 0, 255),
									Math.Clamp(codes[i + 4], 0, 255))
							};
						i += 4;
					}
				}
			}
		}

		static int FindControlEnd(string text, int start)
		{
			for (var i = start; i < text.Length; i++)
				if (text[i] is >= '@' and <= '~')
					return i;

			return -1;
		}

		static int FindOscEnd(string text, int start)
		{
			for (var i = start; i < text.Length; i++)
			{
				if (text[i] == '\x07')
					return i + 1;
				if (text[i] == '\x1B' && i + 1 < text.Length && text[i + 1] == '\\')
					return i + 2;
			}

			return -1;
		}

		static Color Color256(int value)
		{
			value = Math.Clamp(value, 0, 255);
			if (value < 8)
				return StandardColors[value];
			if (value < 16)
				return BrightColors[value - 8];
			if (value >= 232)
			{
				var shade = 8 + (value - 232) * 10;
				return Color.FromArgb(shade, shade, shade);
			}

			var index = value - 16;
			var levels = new[] { 0, 95, 135, 175, 215, 255 };
			return Color.FromArgb(
				levels[index / 36],
				levels[index / 6 % 6],
				levels[index % 6]);
		}
	}
}
