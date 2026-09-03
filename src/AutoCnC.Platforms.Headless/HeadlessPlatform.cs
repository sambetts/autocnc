#region Copyright & License Information
/*
 * Copyright (c) The AutoC&C Developers and Contributors
 * This file is part of AutoC&C, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * (at your option) any later version. For more information, see LICENSE.
 */
#endregion

using System;
using System.IO;
using OpenRA;
using OpenRA.Primitives;

namespace OpenRA.Platforms.Headless
{
	public sealed class HeadlessPlatform : IPlatform
	{
		public IPlatformWindow CreateWindow(
			Size size, WindowMode windowMode, float scaleModifier, int vertexBatchSize,
			int indexBatchSize, int videoDisplay, GLProfile profile) =>
			new HeadlessWindow(size, scaleModifier);

		public ISoundEngine CreateSound(string device) => new HeadlessSoundEngine();

		public IFont CreateFont(byte[] data) => new HeadlessFont();
	}

	sealed class HeadlessWindow : IPlatformWindow
	{
		readonly HeadlessGraphicsContext context = new();
		readonly Size size;
		float scale;
		string clipboard = "";

		public HeadlessWindow(Size size, float scale)
		{
			this.size = size;
			this.scale = scale > 0 ? scale : 1;
		}

		public IGraphicsContext Context => context;
		public Size NativeWindowSize => size;
		public Size EffectiveWindowSize => new(
			Math.Max(1, (int)(size.Width / scale)),
			Math.Max(1, (int)(size.Height / scale)));
		public float NativeWindowScale => 1;
		public float EffectiveWindowScale => scale;
		public Size SurfaceSize => size;
		public int DisplayCount => 1;
		public int CurrentDisplay => 0;
		public bool HasInputFocus => false;
		public bool IsSuspended => true;
		public GLProfile GLProfile => GLProfile.Modern;
		public GLProfile[] SupportedGLProfiles => [GLProfile.Modern];

		public event Action<float, float, float, float> OnWindowScaleChanged
		{
			add { }
			remove { }
		}

		public void PumpInput(IInputHandler inputHandler) { }
		public string GetClipboardText() => clipboard;

		public bool SetClipboardText(string text)
		{
			clipboard = text ?? "";
			return true;
		}

		public bool TryOpenUrl(string url) => false;
		public void GrabWindowMouseFocus() { }
		public void ReleaseWindowMouseFocus() { }
		public IHardwareCursor CreateHardwareCursor(
			string name, Size cursorSize, byte[] data, int2 hotspot, bool pixelDouble) =>
			new HeadlessHardwareCursor();
		public void SetHardwareCursor(IHardwareCursor cursor) { }
		public void SetWindowTitle(string title) { }
		public void SetRelativeMouseMode(bool mode) { }
		public void SetScaleModifier(float value) => scale = value > 0 ? value : 1;
		public void Dispose() => context.Dispose();
	}

	sealed class HeadlessGraphicsContext : IGraphicsContext
	{
		public string GLVersion => "AutoC&C headless";

		public IVertexBuffer<T> CreateEmptyVertexBuffer<T>(int size) where T : struct =>
			new HeadlessVertexBuffer<T>();

		public IVertexBuffer<T> CreateVertexBuffer<T>(T[] data, bool dynamic = true) where T : struct =>
			new HeadlessVertexBuffer<T>();

		public T[] CreateVertices<T>(int size) where T : struct => new T[size];

		public IIndexBuffer CreateIndexBuffer(uint[] indices) => new HeadlessIndexBuffer();
		public ITexture CreateTexture() => new HeadlessTexture();
		public IFrameBuffer CreateFrameBuffer(Size size) => new HeadlessFrameBuffer(size);
		public IFrameBuffer CreateFrameBuffer(Size size, Color clearColor) => new HeadlessFrameBuffer(size);
		public IShader CreateShader(IShaderBindings shaderBindings) => new HeadlessShader();
		public void EnableScissor(int x, int y, int width, int height) { }
		public void DisableScissor() { }
		public void Present() { }
		public void DrawPrimitives(PrimitiveType primitiveType, int firstVertex, int numVertices) { }
		public void DrawElements(int numIndices, int offset) { }
		public void Clear() { }
		public void EnableDepthBuffer() { }
		public void DisableDepthBuffer() { }
		public void ClearDepthBuffer() { }
		public void SetBlendMode(BlendMode mode) { }
		public void SetVSyncEnabled(bool enabled) { }
		public void Dispose() { }
	}

	sealed class HeadlessVertexBuffer<T> : IVertexBuffer<T> where T : struct
	{
		public void Bind() { }
		public void SetData(T[] vertices, int length) { }
		public void SetData(ref T[] vertices, int length) { }
		public void SetData(T[] vertices, int offset, int start, int length) { }
		public void Dispose() { }
	}

	sealed class HeadlessIndexBuffer : IIndexBuffer
	{
		public void Bind() { }
		public void Dispose() { }
	}

	sealed class HeadlessTexture : ITexture
	{
		public Size Size { get; private set; }
		public TextureScaleFilter ScaleFilter { get; set; }

		public void SetData(byte[] colors, int width, int height) => Size = new Size(width, height);
		public void SetFloatData(float[] data, int width, int height) => Size = new Size(width, height);
		public void SetDataFromReadBuffer(Rectangle rect) => Size = rect.Size;
		public byte[] GetData() => new byte[checked(4 * Size.Width * Size.Height)];
		public void Dispose() { }

		public void Resize(Size value) => Size = value;
	}

	sealed class HeadlessFrameBuffer : IFrameBuffer
	{
		readonly HeadlessTexture texture = new();

		public HeadlessFrameBuffer(Size size)
		{
			texture.Resize(size);
		}

		public ITexture Texture => texture;
		public void Bind() { }
		public void Unbind() { }
		public void EnableScissor(Rectangle rect) { }
		public void DisableScissor() { }
		public void Dispose() => texture.Dispose();
	}

	sealed class HeadlessShader : IShader
	{
		public void SetBool(string name, bool value) { }
		public void SetVec(string name, float x) { }
		public void SetVec(string name, float x, float y) { }
		public void SetVec(string name, float x, float y, float z) { }
		public void SetVec(string name, ReadOnlyMemory<float> vec, int length) { }
		public void SetTexture(string parameter, ITexture texture) { }
		public void SetMatrix(string parameter, float[] matrix) { }
		public void PrepareRender() { }
		public void Bind() { }
	}

	sealed class HeadlessHardwareCursor : IHardwareCursor
	{
		public void Dispose() { }
	}

	sealed class HeadlessFont : IFont
	{
		public FontGlyph CreateGlyph(char character, int size, float deviceScale)
		{
			var advance = Math.Max(1, size / 2) * Math.Max(deviceScale, 1);
			return new FontGlyph
			{
				Offset = int2.Zero,
				Size = new Size(1, 1),
				Advance = advance,
				Data = [0]
			};
		}

		public void Dispose() { }
	}

	sealed class HeadlessSoundEngine : ISoundEngine
	{
		public bool Dummy => true;
		public float Volume { get; set; }
		public SoundDevice[] AvailableDevices() => [new SoundDevice(null, "No Sound Output")];
		public ISoundSource AddSoundSourceFromMemory(byte[] data, int channels, int sampleBits, int sampleRate) =>
			new HeadlessSoundSource();
		public ISound Play2D(
			ISoundSource sound, bool loop, bool relative, WPos position, float volume, bool attenuateVolume) =>
			new HeadlessSound();
		public ISound Play2DStream(
			Stream stream, int channels, int sampleBits, int sampleRate, bool loop, bool relative,
			WPos position, float volume) => new HeadlessSound();
		public void PauseSound(ISound sound, bool paused) { }
		public void StopSound(ISound sound) { }
		public void SetAllSoundsPaused(bool paused) { }
		public void StopAllSounds() { }
		public void SetListenerPosition(WPos position) { }
		public void SetSoundVolume(float volume, ISound music, ISound video) { }
		public void SetSoundLooping(bool looping, ISound sound) { }
		public void SetSoundPosition(ISound sound, WPos position) { }
		public void Dispose() { }
	}

	sealed class HeadlessSoundSource : ISoundSource
	{
		public void Dispose() { }
	}

	sealed class HeadlessSound : ISound
	{
		public float Volume { get; set; }
		public float SeekPosition => 0;
		public bool Complete => false;
		public void SetPosition(WPos position) { }
	}
}
