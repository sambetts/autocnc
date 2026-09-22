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
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using AutoCnC.Launcher;

namespace AutoCnC.Training
{
	static class Program
	{
		static int Main(string[] args)
		{
			Console.OutputEncoding = Encoding.UTF8;
			if (args.Length == 1 && args[0] == "--help")
			{
				Console.WriteLine("Run scripts/train-loop.ps1 [-BattleBot Reference] [-Map 16-9.oramap] [-Rounds 0].");
				Console.WriteLine("Use Get-Help ./scripts/train-loop.ps1 -Detailed for battle, agent and recovery options.");
				return 0;
			}
			if (args.Length != 2 || args[0] != "--options")
			{
				Console.Error.WriteLine("Use scripts/train-loop.ps1, or pass --options <configuration.json>.");
				return 2;
			}

			using var cancellation = new CancellationTokenSource();
			ConsoleCancelEventHandler stop = (_, e) =>
			{
				e.Cancel = true;
				Console.Error.WriteLine("Stopping training and its worker processes...");
				cancellation.Cancel();
			};
			Console.CancelKeyPress += stop;
			try
			{
				var options = JsonSerializer.Deserialize<TrainingLoopOptions>(File.ReadAllText(args[1]),
					new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
					throw new ArgumentException("Training options cannot be null.");

				// Asked for and available are different questions, and only the console can
				// answer the second one.
				options.Color = options.Color && ConsoleAnsi.TryEnable();
				new TrainingLoopRunner(options, Console.WriteLine).Run(cancellation.Token);
				return 0;
			}
			catch (OperationCanceledException)
			{
				Console.Error.WriteLine("Training stopped. Saved evidence and source snapshots were retained.");
				return 130;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("Training stopped: " + ex.Message);
				return 1;
			}
			finally
			{
				Console.CancelKeyPress -= stop;
			}
		}
	}
}
