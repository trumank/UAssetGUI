using ImGuiNET;
using imnodesNET;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL4;
//using OpenTK.Mathematics;
//using OpenTK.Windowing.Common.Input;
//using OpenTK.Windowing.Desktop;
//using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Input;
using System.Diagnostics;
using ErrorCode = OpenTK.Graphics.OpenGL4.ErrorCode;
using OpenTK;
using System.Windows.Forms;

namespace UAssetAPI
{
    public class ImGuiController : IDisposable
    {
        private bool _frameBegun;

        private int _vertexArray;
        private int _vertexBuffer;
        private int _vertexBufferSize;
        private int _indexBuffer;
        private int _indexBufferSize;

        //private Texture _fontTexture;

        private int _fontTexture;

        private int _shader;
        private int _shaderFontTextureLocation;
        private int _shaderProjectionMatrixLocation;
        
        private int _windowWidth;
        private int _windowHeight;

        private System.Numerics.Vector2 _scaleFactor = System.Numerics.Vector2.One;

        private static bool KHRDebugAvailable = false;

        public IntPtr context;

        private Control control;
        private Stopwatch stopwatch = new Stopwatch();

        /// <summary>
        /// Constructs a new ImGuiController.
        /// </summary>
        public ImGuiController(Control control)
        {
            this.control = control;

            _windowWidth = Math.Max(1, control.Width);
            _windowHeight = Math.Max(1, control.Height);

            control.KeyDown += OnKeyDown;
            control.KeyUp += OnKeyUp;
            control.KeyPress += OnKeyPress;
            control.MouseWheel += OnMouseWheel;
            control.LostFocus += OnLostFocus;

            int major = GL.GetInteger(GetPName.MajorVersion);
            int minor = GL.GetInteger(GetPName.MinorVersion);

            KHRDebugAvailable = (major == 4 && minor >= 3) || IsExtensionSupported("KHR_debug");

            context = ImGui.CreateContext();
            ImGui.SetCurrentContext(context);
            var io = ImGui.GetIO();
            io.Fonts.AddFontDefault();
            unsafe { io.NativePtr->IniFilename = null; }

            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

            CreateDeviceResources();
            SetKeyMappings();

            SetPerFrameImGuiData(1f / 60f);

            ImGui.NewFrame();
            _frameBegun = true;
        }

        private void OnKeyDown(object sender, KeyEventArgs args)
        {
            ImGui.GetIO().KeysDown[args.KeyValue] = true;
        }
        private void OnKeyUp(object sender, KeyEventArgs args)
        {
            ImGui.GetIO().KeysDown[args.KeyValue] = false;
        }
        private void OnKeyPress(object sender, System.Windows.Forms.KeyPressEventArgs args)
        {
            PressChar(args.KeyChar);
        }
        private void OnMouseWheel(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.MouseWheel = e.Delta / SystemInformation.MouseWheelScrollDelta;
        }
        private void OnLostFocus(object sender, EventArgs e)
        {
            var keysDown = ImGui.GetIO().KeysDown;
            for (var i = 0; i < keysDown.Count; i++)
                keysDown[i] = false;
        }

        public void WindowResized(int width, int height)
        {
            _windowWidth = width;
            _windowHeight = height;
        }

        public void DestroyDeviceObjects()
        {
            Dispose();
        }

        public void CreateDeviceResources()
        {
            _vertexBufferSize = 10000;
            _indexBufferSize = 2000;

            int prevVAO = GL.GetInteger(GetPName.VertexArrayBinding);
            int prevArrayBuffer = GL.GetInteger(GetPName.ArrayBufferBinding);

            _vertexArray = GL.GenVertexArray();
            GL.BindVertexArray(_vertexArray);
            LabelObject(ObjectLabelIdentifier.VertexArray, _vertexArray, "ImGui");

            _vertexBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            LabelObject(ObjectLabelIdentifier.Buffer, _vertexBuffer, "VBO: ImGui");
            GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            _indexBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            LabelObject(ObjectLabelIdentifier.Buffer, _indexBuffer, "EBO: ImGui");
            GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            RecreateFontDeviceTexture();

            string VertexSource = @"#version 330 core

uniform mat4 projection_matrix;

layout(location = 0) in vec2 in_position;
layout(location = 1) in vec2 in_texCoord;
layout(location = 2) in vec4 in_color;

out vec4 color;
out vec2 texCoord;

void main()
{
    gl_Position = projection_matrix * vec4(in_position, 0, 1);
    color = in_color;
    texCoord = in_texCoord;
}";
            string FragmentSource = @"#version 330 core

uniform sampler2D in_fontTexture;

in vec4 color;
in vec2 texCoord;

out vec4 outputColor;

void main()
{
    outputColor = color * texture(in_fontTexture, texCoord);
}";

            _shader = CreateProgram("ImGui", VertexSource, FragmentSource);
            _shaderProjectionMatrixLocation = GL.GetUniformLocation(_shader, "projection_matrix");
            _shaderFontTextureLocation = GL.GetUniformLocation(_shader, "in_fontTexture");

            int stride = Unsafe.SizeOf<ImDrawVert>();
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);

            GL.EnableVertexAttribArray(0);
            GL.EnableVertexAttribArray(1);
            GL.EnableVertexAttribArray(2);

            GL.BindVertexArray(prevVAO);
            GL.BindBuffer(BufferTarget.ArrayBuffer, prevArrayBuffer);

            CheckGLError("End of ImGui setup");
        }

        /// <summary>
        /// Recreates the device texture used to render text.
        /// </summary>
        public void RecreateFontDeviceTexture()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);

            int mips = (int)Math.Floor(Math.Log(Math.Max(width, height), 2));

            int prevActiveTexture = GL.GetInteger(GetPName.ActiveTexture);
            GL.ActiveTexture(TextureUnit.Texture0);
            int prevTexture2D = GL.GetInteger(GetPName.TextureBinding2D);

            _fontTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
            GL.TexStorage2D(TextureTarget2d.Texture2D, mips, SizedInternalFormat.Rgba8, width, height);
            LabelObject(ObjectLabelIdentifier.Texture, _fontTexture, "ImGui Text Atlas");

            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height, PixelFormat.Bgra, PixelType.UnsignedByte, pixels);

            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, mips - 1);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);

            // Restore state
            GL.BindTexture(TextureTarget.Texture2D, prevTexture2D);
            GL.ActiveTexture((TextureUnit)prevActiveTexture);

            io.Fonts.SetTexID((IntPtr)_fontTexture);

            io.Fonts.ClearTexData();
        }

        /// <summary>
        /// Renders the ImGui draw list data.
        /// </summary>
        public void Render()
        {
            if (_frameBegun)
            {
                _frameBegun = false;
                ImGui.Render();
                RenderImDrawData(ImGui.GetDrawData());
            }
        }

        /// <summary>
        /// Updates ImGui input and IO configuration state.
        /// </summary>
        public void Update()
        {
            if (_frameBegun)
            {
                ImGui.Render();
            }

            SetPerFrameImGuiData((float) stopwatch.Elapsed.TotalMilliseconds / 1000);
            stopwatch.Restart();
            UpdateImGuiInput();

            _frameBegun = true;
            ImGui.NewFrame();
        }

        /// <summary>
        /// Sets per-frame data based on the associated window.
        /// This is called by Update(float).
        /// </summary>
        private void SetPerFrameImGuiData(float deltaSeconds)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new System.Numerics.Vector2(
                _windowWidth / _scaleFactor.X,
                _windowHeight / _scaleFactor.Y);
            io.DisplayFramebufferScale = _scaleFactor;
            io.DeltaTime = deltaSeconds; // DeltaTime is in seconds.
        }

        readonly List<char> PressedChars = new List<char>();

        private void UpdateImGuiInput()
        {
            ImGuiIOPtr io = ImGui.GetIO();

            io.MouseDown[0] = Control.MouseButtons == MouseButtons.Left;
            io.MouseDown[1] = Control.MouseButtons == MouseButtons.Right;
            io.MouseDown[2] = Control.MouseButtons == MouseButtons.Middle;

            var clientPos = control.PointToClient(Control.MousePosition);
            io.MousePos = new System.Numerics.Vector2(clientPos.X, clientPos.Y);

            foreach (var c in PressedChars)
                io.AddInputCharacter(c);
            PressedChars.Clear();

            io.KeyCtrl = 0 != (Control.ModifierKeys & Keys.Control);
            io.KeyAlt = 0 != (Control.ModifierKeys & Keys.Alt);
            io.KeyShift = 0 != (Control.ModifierKeys & Keys.Shift);
            io.KeySuper = 0 != (Control.ModifierKeys & (Keys.LWin | Keys.RWin));
        }

        internal void PressChar(char keyChar)
        {
            PressedChars.Add(keyChar);
        }

        private static void SetKeyMappings()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.KeyMap[(int)ImGuiKey.Tab] = (int)Keys.Tab;
            io.KeyMap[(int)ImGuiKey.LeftArrow] = (int)Keys.Left;
            io.KeyMap[(int)ImGuiKey.RightArrow] = (int)Keys.Right;
            io.KeyMap[(int)ImGuiKey.UpArrow] = (int)Keys.Up;
            io.KeyMap[(int)ImGuiKey.DownArrow] = (int)Keys.Down;
            io.KeyMap[(int)ImGuiKey.PageUp] = (int)Keys.PageUp;
            io.KeyMap[(int)ImGuiKey.PageDown] = (int)Keys.PageDown;
            io.KeyMap[(int)ImGuiKey.Home] = (int)Keys.Home;
            io.KeyMap[(int)ImGuiKey.End] = (int)Keys.End;
            io.KeyMap[(int)ImGuiKey.Insert] = (int)Keys.Insert;
            io.KeyMap[(int)ImGuiKey.Delete] = (int)Keys.Delete;
            io.KeyMap[(int)ImGuiKey.Backspace] = (int)Keys.Back;
            io.KeyMap[(int)ImGuiKey.Space] = (int)Keys.Space;
            io.KeyMap[(int)ImGuiKey.Enter] = (int)Keys.Enter;
            io.KeyMap[(int)ImGuiKey.Escape] = (int)Keys.Escape;
            io.KeyMap[(int)ImGuiKey.LeftCtrl] = (int)Keys.LControlKey;
            io.KeyMap[(int)ImGuiKey.LeftShift] = (int)Keys.LShiftKey;
            io.KeyMap[(int)ImGuiKey.LeftAlt] = (int)Keys.LMenu;
            io.KeyMap[(int)ImGuiKey.LeftSuper] = (int)Keys.LWin;
            io.KeyMap[(int)ImGuiKey.RightCtrl] = (int)Keys.RControlKey;
            io.KeyMap[(int)ImGuiKey.RightShift] = (int)Keys.RShiftKey;
            io.KeyMap[(int)ImGuiKey.RightAlt] = (int)Keys.RMenu;
            io.KeyMap[(int)ImGuiKey.RightSuper] = (int)Keys.RWin;
            io.KeyMap[(int)ImGuiKey.Menu] = (int)Keys.Menu;
            io.KeyMap[(int)ImGuiKey._0] = (int)Keys.D0;
            io.KeyMap[(int)ImGuiKey._1] = (int)Keys.D1;
            io.KeyMap[(int)ImGuiKey._2] = (int)Keys.D2;
            io.KeyMap[(int)ImGuiKey._3] = (int)Keys.D3;
            io.KeyMap[(int)ImGuiKey._4] = (int)Keys.D4;
            io.KeyMap[(int)ImGuiKey._5] = (int)Keys.D5;
            io.KeyMap[(int)ImGuiKey._6] = (int)Keys.D6;
            io.KeyMap[(int)ImGuiKey._7] = (int)Keys.D7;
            io.KeyMap[(int)ImGuiKey._8] = (int)Keys.D8;
            io.KeyMap[(int)ImGuiKey._9] = (int)Keys.D9;
            io.KeyMap[(int)ImGuiKey.A] = (int)Keys.A;
            io.KeyMap[(int)ImGuiKey.B] = (int)Keys.B;
            io.KeyMap[(int)ImGuiKey.C] = (int)Keys.C;
            io.KeyMap[(int)ImGuiKey.D] = (int)Keys.D;
            io.KeyMap[(int)ImGuiKey.E] = (int)Keys.E;
            io.KeyMap[(int)ImGuiKey.F] = (int)Keys.F;
            io.KeyMap[(int)ImGuiKey.G] = (int)Keys.G;
            io.KeyMap[(int)ImGuiKey.H] = (int)Keys.H;
            io.KeyMap[(int)ImGuiKey.I] = (int)Keys.I;
            io.KeyMap[(int)ImGuiKey.J] = (int)Keys.J;
            io.KeyMap[(int)ImGuiKey.K] = (int)Keys.K;
            io.KeyMap[(int)ImGuiKey.L] = (int)Keys.L;
            io.KeyMap[(int)ImGuiKey.M] = (int)Keys.M;
            io.KeyMap[(int)ImGuiKey.N] = (int)Keys.N;
            io.KeyMap[(int)ImGuiKey.O] = (int)Keys.O;
            io.KeyMap[(int)ImGuiKey.P] = (int)Keys.P;
            io.KeyMap[(int)ImGuiKey.Q] = (int)Keys.Q;
            io.KeyMap[(int)ImGuiKey.R] = (int)Keys.R;
            io.KeyMap[(int)ImGuiKey.S] = (int)Keys.S;
            io.KeyMap[(int)ImGuiKey.T] = (int)Keys.T;
            io.KeyMap[(int)ImGuiKey.U] = (int)Keys.U;
            io.KeyMap[(int)ImGuiKey.V] = (int)Keys.V;
            io.KeyMap[(int)ImGuiKey.W] = (int)Keys.W;
            io.KeyMap[(int)ImGuiKey.X] = (int)Keys.X;
            io.KeyMap[(int)ImGuiKey.Y] = (int)Keys.Y;
            io.KeyMap[(int)ImGuiKey.Z] = (int)Keys.Z;
            io.KeyMap[(int)ImGuiKey.F1] = (int)Keys.F1;
            io.KeyMap[(int)ImGuiKey.F2] = (int)Keys.F2;
            io.KeyMap[(int)ImGuiKey.F3] = (int)Keys.F3;
            io.KeyMap[(int)ImGuiKey.F4] = (int)Keys.F4;
            io.KeyMap[(int)ImGuiKey.F5] = (int)Keys.F5;
            io.KeyMap[(int)ImGuiKey.F6] = (int)Keys.F6;
            io.KeyMap[(int)ImGuiKey.F7] = (int)Keys.F7;
            io.KeyMap[(int)ImGuiKey.F8] = (int)Keys.F8;
            io.KeyMap[(int)ImGuiKey.F9] = (int)Keys.F9;
            io.KeyMap[(int)ImGuiKey.F10] = (int)Keys.F10;
            io.KeyMap[(int)ImGuiKey.F11] = (int)Keys.F11;
            io.KeyMap[(int)ImGuiKey.F12] = (int)Keys.F12;
            io.KeyMap[(int)ImGuiKey.Apostrophe] = (int)Keys.OemQuotes;
            io.KeyMap[(int)ImGuiKey.Comma] = (int)Keys.Oemcomma;
            io.KeyMap[(int)ImGuiKey.Minus] = (int)Keys.OemMinus;
            io.KeyMap[(int)ImGuiKey.Period] = (int)Keys.OemPeriod;
            io.KeyMap[(int)ImGuiKey.Slash] = (int)Keys.OemQuestion;
            io.KeyMap[(int)ImGuiKey.Semicolon] = (int)Keys.OemSemicolon;
            io.KeyMap[(int)ImGuiKey.Equal] = (int)Keys.Oemplus;
            io.KeyMap[(int)ImGuiKey.LeftBracket] = (int)Keys.OemOpenBrackets;
            io.KeyMap[(int)ImGuiKey.Backslash] = (int)Keys.OemBackslash;
            io.KeyMap[(int)ImGuiKey.RightBracket] = (int)Keys.OemCloseBrackets;
            io.KeyMap[(int)ImGuiKey.GraveAccent] = (int)Keys.Oemtilde;
            io.KeyMap[(int)ImGuiKey.CapsLock] = (int)Keys.CapsLock;
            io.KeyMap[(int)ImGuiKey.ScrollLock] = (int)Keys.Scroll;
            io.KeyMap[(int)ImGuiKey.NumLock] = (int)Keys.NumLock;
            io.KeyMap[(int)ImGuiKey.PrintScreen] = (int)Keys.PrintScreen;
            io.KeyMap[(int)ImGuiKey.Pause] = (int)Keys.Pause;
            io.KeyMap[(int)ImGuiKey.Keypad0] = (int)Keys.NumPad0;
            io.KeyMap[(int)ImGuiKey.Keypad1] = (int)Keys.NumPad1;
            io.KeyMap[(int)ImGuiKey.Keypad2] = (int)Keys.NumPad2;
            io.KeyMap[(int)ImGuiKey.Keypad3] = (int)Keys.NumPad3;
            io.KeyMap[(int)ImGuiKey.Keypad4] = (int)Keys.NumPad4;
            io.KeyMap[(int)ImGuiKey.Keypad5] = (int)Keys.NumPad5;
            io.KeyMap[(int)ImGuiKey.Keypad6] = (int)Keys.NumPad6;
            io.KeyMap[(int)ImGuiKey.Keypad7] = (int)Keys.NumPad7;
            io.KeyMap[(int)ImGuiKey.Keypad8] = (int)Keys.NumPad8;
            io.KeyMap[(int)ImGuiKey.Keypad9] = (int)Keys.NumPad9;
            io.KeyMap[(int)ImGuiKey.KeypadDecimal] = (int)Keys.Decimal;
            io.KeyMap[(int)ImGuiKey.KeypadDivide] = (int)Keys.Divide;
            io.KeyMap[(int)ImGuiKey.KeypadMultiply] = (int)Keys.Multiply;
            io.KeyMap[(int)ImGuiKey.KeypadSubtract] = (int)Keys.Subtract;
            io.KeyMap[(int)ImGuiKey.KeypadAdd] = (int)Keys.Add;
            io.KeyMap[(int)ImGuiKey.KeypadEnter] = (int)Keys.Enter;
            //io.KeyMap[(int)ImGuiKey.KeypadEqual] = (int)Keys.Equal;
        }

        private void RenderImDrawData(ImDrawDataPtr draw_data)
        {
            if (draw_data.CmdListsCount == 0)
            {
                return;
            }

            // Get intial state.
            int prevVAO = GL.GetInteger(GetPName.VertexArrayBinding);
            int prevArrayBuffer = GL.GetInteger(GetPName.ArrayBufferBinding);
            int prevProgram = GL.GetInteger(GetPName.CurrentProgram);
            bool prevBlendEnabled = GL.GetBoolean(GetPName.Blend);
            bool prevScissorTestEnabled = GL.GetBoolean(GetPName.ScissorTest);
            int prevBlendEquationRgb = GL.GetInteger(GetPName.BlendEquationRgb);
            int prevBlendEquationAlpha = GL.GetInteger(GetPName.BlendEquationAlpha);
            int prevBlendFuncSrcRgb = GL.GetInteger(GetPName.BlendSrcRgb);
            int prevBlendFuncSrcAlpha = GL.GetInteger(GetPName.BlendSrcAlpha);
            int prevBlendFuncDstRgb = GL.GetInteger(GetPName.BlendDstRgb);
            int prevBlendFuncDstAlpha = GL.GetInteger(GetPName.BlendDstAlpha);
            bool prevCullFaceEnabled = GL.GetBoolean(GetPName.CullFace);
            bool prevDepthTestEnabled = GL.GetBoolean(GetPName.DepthTest);
            int prevActiveTexture = GL.GetInteger(GetPName.ActiveTexture);
            GL.ActiveTexture(TextureUnit.Texture0);
            int prevTexture2D = GL.GetInteger(GetPName.TextureBinding2D);
            Span<int> prevScissorBox = stackalloc int[4];
            unsafe
            {
                fixed (int* iptr = &prevScissorBox[0])
                {
                    GL.GetInteger(GetPName.ScissorBox, iptr);
                }
            }

            // Bind the element buffer (thru the VAO) so that we can resize it.
            GL.BindVertexArray(_vertexArray);
            // Bind the vertex buffer so that we can resize it.
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            for (int i = 0; i < draw_data.CmdListsCount; i++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdListsRange[i];

                int vertexSize = cmd_list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
                if (vertexSize > _vertexBufferSize)
                {
                    int newSize = (int)Math.Max(_vertexBufferSize * 1.5f, vertexSize);
                    
                    GL.BufferData(BufferTarget.ArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    _vertexBufferSize = newSize;

                    Console.WriteLine($"Resized dear imgui vertex buffer to new size {_vertexBufferSize}");
                }

                int indexSize = cmd_list.IdxBuffer.Size * sizeof(ushort);
                if (indexSize > _indexBufferSize)
                {
                    int newSize = (int)Math.Max(_indexBufferSize * 1.5f, indexSize);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, newSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                    _indexBufferSize = newSize;

                    Console.WriteLine($"Resized dear imgui index buffer to new size {_indexBufferSize}");
                }
            }

            // Setup orthographic projection matrix into our constant buffer
            ImGuiIOPtr io = ImGui.GetIO();
            Matrix4 mvp = Matrix4.CreateOrthographicOffCenter(
                0.0f,
                io.DisplaySize.X,
                io.DisplaySize.Y,
                0.0f,
                -1.0f,
                1.0f);

            GL.UseProgram(_shader);
            GL.UniformMatrix4(_shaderProjectionMatrixLocation, false, ref mvp);
            GL.Uniform1(_shaderFontTextureLocation, 0);
            CheckGLError("Projection");

            GL.BindVertexArray(_vertexArray);
            CheckGLError("VAO");

            draw_data.ScaleClipRects(io.DisplayFramebufferScale);

            GL.Enable(EnableCap.Blend);
            GL.Enable(EnableCap.ScissorTest);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);

            // Render command lists
            for (int n = 0; n < draw_data.CmdListsCount; n++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdListsRange[n];

                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, cmd_list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>(), cmd_list.VtxBuffer.Data);
                CheckGLError($"Data Vert {n}");

                GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, cmd_list.IdxBuffer.Size * sizeof(ushort), cmd_list.IdxBuffer.Data);
                CheckGLError($"Data Idx {n}");

                for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
                {
                    ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
                    if (pcmd.UserCallback != IntPtr.Zero)
                    {
                        throw new NotImplementedException();
                    }
                    else
                    {
                        GL.ActiveTexture(TextureUnit.Texture0);
                        GL.BindTexture(TextureTarget.Texture2D, (int)pcmd.TextureId);
                        CheckGLError("Texture");

                        // We do _windowHeight - (int)clip.W instead of (int)clip.Y because gl has flipped Y when it comes to these coordinates
                        var clip = pcmd.ClipRect;
                        GL.Scissor((int)clip.X, _windowHeight - (int)clip.W, (int)(clip.Z - clip.X), (int)(clip.W - clip.Y));
                        CheckGLError("Scissor");

                        if ((io.BackendFlags & ImGuiBackendFlags.RendererHasVtxOffset) != 0)
                        {
                            GL.DrawElementsBaseVertex(PrimitiveType.Triangles, (int)pcmd.ElemCount, DrawElementsType.UnsignedShort, (IntPtr)(pcmd.IdxOffset * sizeof(ushort)), unchecked((int)pcmd.VtxOffset));
                        }
                        else
                        {
                            GL.DrawElements(BeginMode.Triangles, (int)pcmd.ElemCount, DrawElementsType.UnsignedShort, (int)pcmd.IdxOffset * sizeof(ushort));
                        }
                        CheckGLError("Draw");
                    }
                }
            }

            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);

            // Reset state
            GL.BindTexture(TextureTarget.Texture2D, prevTexture2D);
            GL.ActiveTexture((TextureUnit)prevActiveTexture);
            GL.UseProgram(prevProgram);
            GL.BindVertexArray(prevVAO);
            GL.Scissor(prevScissorBox[0], prevScissorBox[1], prevScissorBox[2], prevScissorBox[3]);
            GL.BindBuffer(BufferTarget.ArrayBuffer, prevArrayBuffer);
            GL.BlendEquationSeparate((BlendEquationMode)prevBlendEquationRgb, (BlendEquationMode)prevBlendEquationAlpha);
            GL.BlendFuncSeparate(
                (BlendingFactorSrc)prevBlendFuncSrcRgb,
                (BlendingFactorDest)prevBlendFuncDstRgb,
                (BlendingFactorSrc)prevBlendFuncSrcAlpha,
                (BlendingFactorDest)prevBlendFuncDstAlpha);
            if (prevBlendEnabled) GL.Enable(EnableCap.Blend); else GL.Disable(EnableCap.Blend);
            if (prevDepthTestEnabled) GL.Enable(EnableCap.DepthTest); else GL.Disable(EnableCap.DepthTest);
            if (prevCullFaceEnabled) GL.Enable(EnableCap.CullFace); else GL.Disable(EnableCap.CullFace);
            if (prevScissorTestEnabled) GL.Enable(EnableCap.ScissorTest); else GL.Disable(EnableCap.ScissorTest);
        }

        /// <summary>
        /// Frees all graphics resources used by the renderer.
        /// </summary>
        public void Dispose()
        {
            GL.DeleteVertexArray(_vertexArray);
            GL.DeleteBuffer(_vertexBuffer);
            GL.DeleteBuffer(_indexBuffer);

            GL.DeleteTexture(_fontTexture);
            GL.DeleteProgram(_shader);
        }

        public static void LabelObject(ObjectLabelIdentifier objLabelIdent, int glObject, string name)
        {
            if (KHRDebugAvailable)
                GL.ObjectLabel(objLabelIdent, glObject, name.Length, name);
        }

        static bool IsExtensionSupported(string name)
        {
            int n = GL.GetInteger(GetPName.NumExtensions);
            for (int i = 0; i < n; i++)
            {
                string extension = GL.GetString(StringNameIndexed.Extensions, i);
                if (extension == name) return true;
            }

            return false;
        }

        public static int CreateProgram(string name, string vertexSource, string fragmentSoruce)
        {
            int program = GL.CreateProgram();
            LabelObject(ObjectLabelIdentifier.Program, program, $"Program: {name}");

            int vertex = CompileShader(name, ShaderType.VertexShader, vertexSource);
            int fragment = CompileShader(name, ShaderType.FragmentShader, fragmentSoruce);

            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);

            GL.LinkProgram(program);

            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int success);
            if (success == 0)
            {
                string info = GL.GetProgramInfoLog(program);
                Debug.WriteLine($"GL.LinkProgram had info log [{name}]:\n{info}");
            }

            GL.DetachShader(program, vertex);
            GL.DetachShader(program, fragment);

            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);

            return program;
        }

        private static int CompileShader(string name, ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            LabelObject(ObjectLabelIdentifier.Shader, shader, $"Shader: {name}");

            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);

            GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
            if (success == 0)
            {
                string info = GL.GetShaderInfoLog(shader);
                Debug.WriteLine($"GL.CompileShader for shader '{name}' [{type}] had info log:\n{info}");
            }

            return shader;
        }

        public static void CheckGLError(string title)
        {
            ErrorCode error;
            int i = 1;
            while ((error = GL.GetError()) != ErrorCode.NoError)
            {
                Debug.Print($"{title} ({i++}): {error}");
            }
        }
    }
}
