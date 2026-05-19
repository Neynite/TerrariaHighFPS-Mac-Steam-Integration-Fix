using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace HighFPSLogic
{
    // Per-sprite instance record: 56 bytes
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct InstanceData
    {
        public float SrcU, SrcV, SrcUW, SrcVH;          // normalized UV rect
        public float DstX, DstY, DstW, DstH;             // destination screen rect (pixels)
        public float OriginX, OriginY, RotSin, RotCos;   // origin (pixels) + precomputed rotation
        public uint  Color;                               // packed RGBA (R at byte 0)
        public float Effects;                             // 0=none,1=flipH,2=flipV,3=both
    }

    internal static class GL
    {
        static readonly string LIB = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "opengl32.dll" : "libGL.so.1";

        [DllImport("libGL.so.1")] internal static extern uint  glCreateShader(uint type);
        [DllImport("libGL.so.1")] internal static extern void  glShaderSource(uint shader, int count, string[] src, int[] lengths);
        [DllImport("libGL.so.1")] internal static extern void  glCompileShader(uint shader);
        [DllImport("libGL.so.1")] internal static extern uint  glCreateProgram();
        [DllImport("libGL.so.1")] internal static extern void  glAttachShader(uint prog, uint shader);
        [DllImport("libGL.so.1")] internal static extern void  glLinkProgram(uint prog);
        [DllImport("libGL.so.1")] internal static extern void  glUseProgram(uint prog);
        [DllImport("libGL.so.1")] internal static extern int   glGetUniformLocation(uint prog, string name);
        [DllImport("libGL.so.1")] internal static extern void  glUniformMatrix4fv(int loc, int count, byte transpose, ref float value);
        [DllImport("libGL.so.1")] internal static extern void  glUniform1i(int loc, int value);
        [DllImport("libGL.so.1")] internal static extern void  glGenBuffers(int n, uint[] bufs);
        [DllImport("libGL.so.1")] internal static extern void  glBindBuffer(uint target, uint buf);
        [DllImport("libGL.so.1")] internal static extern void  glBufferData(uint target, IntPtr size, IntPtr data, uint usage);
        [DllImport("libGL.so.1")] internal static extern void  glBufferSubData(uint target, IntPtr off, IntPtr size, IntPtr data);
        [DllImport("libGL.so.1")] internal static extern void  glGenVertexArrays(int n, uint[] arrays);
        [DllImport("libGL.so.1")] internal static extern void  glBindVertexArray(uint vao);
        [DllImport("libGL.so.1")] internal static extern void  glVertexAttribPointer(uint idx, int size, uint type, byte normalized, int stride, IntPtr offset);
        [DllImport("libGL.so.1")] internal static extern void  glVertexAttribDivisor(uint idx, uint divisor);
        [DllImport("libGL.so.1")] internal static extern void  glEnableVertexAttribArray(uint idx);
        [DllImport("libGL.so.1")] internal static extern void  glDrawArraysInstanced(uint mode, int first, int count, int instancecount);
        [DllImport("libGL.so.1")] internal static extern void  glGetIntegerv(uint pname, out int data);
        [DllImport("libGL.so.1")] internal static extern void  glGetShaderiv(uint shader, uint pname, out int param);
        [DllImport("libGL.so.1")] internal static extern void  glGetShaderInfoLog(uint shader, int max, out int len, System.Text.StringBuilder log);
        [DllImport("libGL.so.1")] internal static extern void  glGetProgramiv(uint prog, uint pname, out int param);
        [DllImport("libGL.so.1")] internal static extern void  glGetProgramInfoLog(uint prog, int max, out int len, System.Text.StringBuilder log);

        internal const uint GL_VERTEX_SHADER   = 0x8B31;
        internal const uint GL_FRAGMENT_SHADER = 0x8B30;
        internal const uint GL_ARRAY_BUFFER    = 0x8892;
        internal const uint GL_DYNAMIC_DRAW    = 0x88E8;
        internal const uint GL_FLOAT           = 0x1406;
        internal const uint GL_UNSIGNED_BYTE   = 0x1401;
        internal const uint GL_TRIANGLE_STRIP  = 0x0005;
        internal const uint GL_COMPILE_STATUS  = 0x8B81;
        internal const uint GL_LINK_STATUS     = 0x8B82;
        internal const uint GL_CURRENT_PROGRAM      = 0x8B8D;
        internal const uint GL_VERTEX_ARRAY_BINDING = 0x85B5;
        internal const uint GL_ARRAY_BUFFER_BINDING = 0x8894;
    }

    public static class GpuBatchManager
    {
        public static bool IsActive = false;

        public static bool ShouldSkipLight(int x, int y)
        {
            const int margin = 40;
            int sx = (int)(Terraria.Main.screenPosition.X / 16f) - margin;
            int sy = (int)(Terraria.Main.screenPosition.Y / 16f) - margin;
            int ex = sx + Terraria.Main.screenWidth  / 16 + margin * 2;
            int ey = sy + Terraria.Main.screenHeight / 16 + margin * 2;
            return x < sx || x > ex || y < sy || y > ey;
        }

        static bool _initialized = false;
        static bool _initFailed  = false;

        // GL handles
        static uint _program, _vao, _quadVbo, _instanceVbo;
        static int  _uTransform, _uSampler;
        static readonly int InstanceStride = Marshal.SizeOf(typeof(InstanceData));
        const  int MaxInstances = 8192;

        // Per-frame batch accumulation (ordered by first-appearance of each texture)
        struct BatchGroup { public Texture2D Tex; public InstanceData[] Data; public int Count; }
        static BatchGroup[] _groups     = new BatchGroup[256];
        static int          _groupCount = 0;
        static Dictionary<Texture2D, int> _groupIndex = new Dictionary<Texture2D, int>();

        // SpriteBatch reflection (captured once)
        static FieldInfo _fi_sortMode, _fi_blendState, _fi_samplerState,
                         _fi_depthStencilState, _fi_rasterizerState,
                         _fi_customEffect, _fi_transformMatrix, _fi_beginCalled;
        static bool _reflectionReady = false;

        const string VERT_SRC = @"#version 330 core
layout(location=0) in vec2 a_corner;
layout(location=1) in vec4 a_srcUV;
layout(location=2) in vec4 a_dst;
layout(location=3) in vec4 a_origin_rot;
layout(location=4) in vec4 a_color;
layout(location=5) in float a_effects;
uniform mat4 u_transform;
out vec2 v_uv;
out vec4 v_color;
void main() {
    float dstX=a_dst.x, dstY=a_dst.y, dstW=a_dst.z, dstH=a_dst.w;
    float ox=a_origin_rot.x, oy=a_origin_rot.y;
    float rs=a_origin_rot.z, rc=a_origin_rot.w;
    vec2 corner = vec2(a_corner.x*dstW, a_corner.y*dstH);
    vec2 off = corner - vec2(ox, oy);
    vec2 rot = vec2(off.x*rc - off.y*rs, off.x*rs + off.y*rc);
    vec2 screen = vec2(dstX+ox+rot.x, dstY+oy+rot.y);
    gl_Position = u_transform * vec4(screen, 0.0, 1.0);
    int fx = int(a_effects);
    float flipH = float(fx & 1);
    float flipV = float((fx >> 1) & 1);
    float ux = mix(a_corner.x, 1.0-a_corner.x, flipH);
    float uy = mix(a_corner.y, 1.0-a_corner.y, flipV);
    v_uv = vec2(a_srcUV.x + ux*a_srcUV.z, a_srcUV.y + uy*a_srcUV.w);
    v_color = a_color;
}";

        const string FRAG_SRC = @"#version 330 core
uniform sampler2D u_sampler;
in vec2 v_uv;
in vec4 v_color;
out vec4 fragColor;
void main() {
    fragColor = texture(u_sampler, v_uv) * v_color;
}";

        static uint CompileShader(uint type, string src)
        {
            uint shader = GL.glCreateShader(type);
            GL.glShaderSource(shader, 1, new[] { src }, null);
            GL.glCompileShader(shader);
            GL.glGetShaderiv(shader, GL.GL_COMPILE_STATUS, out int ok);
            if (ok == 0)
            {
                var sb = new System.Text.StringBuilder(2048);
                GL.glGetShaderInfoLog(shader, 2048, out _, sb);
                Console.Error.WriteLine("[GpuBatch] Shader compile error: " + sb);
            }
            return shader;
        }

        public static void Initialize()
        {
            if (_initialized || _initFailed) return;
            try
            {
                // Compile program
                uint vs = CompileShader(GL.GL_VERTEX_SHADER,   VERT_SRC);
                uint fs = CompileShader(GL.GL_FRAGMENT_SHADER, FRAG_SRC);
                _program = GL.glCreateProgram();
                GL.glAttachShader(_program, vs);
                GL.glAttachShader(_program, fs);
                GL.glLinkProgram(_program);
                GL.glGetProgramiv(_program, GL.GL_LINK_STATUS, out int linked);
                if (linked == 0)
                {
                    var sb = new System.Text.StringBuilder(2048);
                    GL.glGetProgramInfoLog(_program, 2048, out _, sb);
                    Console.Error.WriteLine("[GpuBatch] Program link error: " + sb);
                    _initFailed = true;
                    return;
                }
                _uTransform = GL.glGetUniformLocation(_program, "u_transform");
                _uSampler   = GL.glGetUniformLocation(_program, "u_sampler");

                // Quad VBO: unit corners for TRIANGLE_STRIP (0,0),(1,0),(0,1),(1,1)
                float[] quad = { 0f, 0f,  1f, 0f,  0f, 1f,  1f, 1f };
                var qbufs = new uint[1];
                GL.glGenBuffers(1, qbufs);
                _quadVbo = qbufs[0];
                GCHandle qpin = GCHandle.Alloc(quad, GCHandleType.Pinned);
                try {
                    GL.glBindBuffer(GL.GL_ARRAY_BUFFER, _quadVbo);
                    GL.glBufferData(GL.GL_ARRAY_BUFFER, new IntPtr(quad.Length * 4), qpin.AddrOfPinnedObject(), GL.GL_DYNAMIC_DRAW);
                } finally { qpin.Free(); }

                // Instance VBO
                var ibufs = new uint[1];
                GL.glGenBuffers(1, ibufs);
                _instanceVbo = ibufs[0];
                GL.glBindBuffer(GL.GL_ARRAY_BUFFER, _instanceVbo);
                GL.glBufferData(GL.GL_ARRAY_BUFFER, new IntPtr(MaxInstances * InstanceStride), IntPtr.Zero, GL.GL_DYNAMIC_DRAW);

                // VAO
                var vaos = new uint[1];
                GL.glGenVertexArrays(1, vaos);
                _vao = vaos[0];
                GL.glBindVertexArray(_vao);

                // Stream 0: quad corners (attrib 0, divisor 0)
                GL.glBindBuffer(GL.GL_ARRAY_BUFFER, _quadVbo);
                GL.glVertexAttribPointer(0, 2, GL.GL_FLOAT, 0, 8, IntPtr.Zero);
                GL.glEnableVertexAttribArray(0);
                GL.glVertexAttribDivisor(0, 0);

                // Stream 1: per-instance data (attribs 1-5, divisor 1)
                GL.glBindBuffer(GL.GL_ARRAY_BUFFER, _instanceVbo);
                int s = InstanceStride;
                GL.glVertexAttribPointer(1, 4, GL.GL_FLOAT, 0, s, IntPtr.Zero);          // srcUV
                GL.glVertexAttribPointer(2, 4, GL.GL_FLOAT, 0, s, new IntPtr(16));        // dst
                GL.glVertexAttribPointer(3, 4, GL.GL_FLOAT, 0, s, new IntPtr(32));        // origin_rot
                GL.glVertexAttribPointer(4, 4, GL.GL_UNSIGNED_BYTE, 1, s, new IntPtr(48)); // color (normalized bytes)
                GL.glVertexAttribPointer(5, 1, GL.GL_FLOAT, 0, s, new IntPtr(52));        // effects
                for (uint a = 1; a <= 5; a++) {
                    GL.glEnableVertexAttribArray(a);
                    GL.glVertexAttribDivisor(a, 1);
                }

                GL.glBindVertexArray(0);

                // Pre-allocate group instance arrays
                for (int i = 0; i < _groups.Length; i++)
                    _groups[i].Data = new InstanceData[512];

                // Cache SpriteBatch reflection fields
                var sbType = typeof(SpriteBatch);
                var bf = BindingFlags.Instance | BindingFlags.NonPublic;
                _fi_sortMode          = sbType.GetField("sortMode",          bf);
                _fi_blendState        = sbType.GetField("blendState",        bf);
                _fi_samplerState      = sbType.GetField("samplerState",      bf);
                _fi_depthStencilState = sbType.GetField("depthStencilState", bf);
                _fi_rasterizerState   = sbType.GetField("rasterizerState",   bf);
                _fi_customEffect      = sbType.GetField("customEffect",      bf);
                _fi_transformMatrix   = sbType.GetField("transformMatrix",   bf);
                _fi_beginCalled       = sbType.GetField("beginCalled",       bf);
                _reflectionReady = _fi_sortMode != null && _fi_blendState != null;

                _initialized = true;
                Console.Error.WriteLine("[GpuBatch] Initialized OK.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[GpuBatch] Initialize failed: " + ex.Message);
                _initFailed = true;
            }
        }

        public static void BeginCapture()
        {
            if (!_initialized) Initialize();
            if (_initFailed) return;
            _groupCount = 0;
            _groupIndex.Clear();
            IsActive = true;
        }

        // Called from injected SpriteBatch.PushSprite intercept in FNA.dll.
        // Returns true if the sprite was consumed (caller should ret immediately).
        public static bool TryRecord(
            Texture2D texture,
            float srcX,  float srcY,  float srcW,  float srcH,
            float dstX,  float dstY,  float dstW,  float dstH,
            Color color,
            float originX, float originY,
            float rotSin,  float rotCos,
            float depth,   byte effects)
        {
            if (!IsActive || _initFailed || texture == null) return false;

            // Normalize source UV
            float tw = texture.Width, th = texture.Height;
            if (tw <= 0 || th <= 0) return false;

            int gi;
            if (!_groupIndex.TryGetValue(texture, out gi))
            {
                if (_groupCount >= _groups.Length) return false;
                gi = _groupCount++;
                _groupIndex[texture] = gi;
                _groups[gi].Tex   = texture;
                _groups[gi].Count = 0;
            }

            ref BatchGroup grp = ref _groups[gi];
            if (grp.Count >= grp.Data.Length)
            {
                // Grow
                var bigger = new InstanceData[grp.Data.Length * 2];
                Array.Copy(grp.Data, bigger, grp.Data.Length);
                grp.Data = bigger;
            }

            ref InstanceData inst = ref grp.Data[grp.Count++];
            inst.SrcU    = srcX / tw;
            inst.SrcV    = srcY / th;
            inst.SrcUW   = srcW / tw;
            inst.SrcVH   = srcH / th;
            inst.DstX    = dstX;
            inst.DstY    = dstY;
            inst.DstW    = dstW;
            inst.DstH    = dstH;
            inst.OriginX = originX;
            inst.OriginY = originY;
            inst.RotSin  = rotSin;
            inst.RotCos  = rotCos;
            inst.Color   = color.PackedValue;
            inst.Effects = effects;
            return true;
        }

        public static void FlushCapture(SpriteBatch sb, GraphicsDeviceManager gdm)
        {
            IsActive = false;
            if (_initFailed || _groupCount == 0) return;

            GraphicsDevice gd;
            try { gd = gdm.GraphicsDevice; } catch { _groupCount = 0; _groupIndex.Clear(); return; }

            // Save GL state we will clobber
            GL.glGetIntegerv(GL.GL_CURRENT_PROGRAM, out int savedProg);
            GL.glGetIntegerv(GL.GL_VERTEX_ARRAY_BINDING, out int savedVao);
            GL.glGetIntegerv(GL.GL_ARRAY_BUFFER_BINDING, out int savedVbo);

            // Orthographic projection matching SpriteBatch's default transform.
            // SpriteBatch's transform: capturedTransformMatrix (usually identity) ×
            //   CreateOrthographicOffCenter(0, vp.W, vp.H, 0, -1, 1).
            // For now we use identity × ortho (handles normal + zoomed camera via
            // capturing the SpriteBatch transformMatrix from reflection).
            Matrix ortho = Matrix.CreateOrthographicOffCenter(
                0, gd.Viewport.Width, gd.Viewport.Height, 0, -1f, 1f);

            if (_reflectionReady && sb != null)
            {
                var tm = _fi_transformMatrix.GetValue(sb);
                if (tm is Matrix capturedTm)
                    ortho = capturedTm * ortho;
            }

            // Bind our program + VAO
            GL.glBindVertexArray(_vao);
            GL.glUseProgram(_program);
            GL.glUniformMatrix4fv(_uTransform, 1, 1 /*transpose*/, ref ortho.M11);
            GL.glUniform1i(_uSampler, 0);

            for (int g = 0; g < _groupCount; g++)
            {
                ref BatchGroup grp = ref _groups[g];
                int count = grp.Count;
                if (count == 0) continue;

                // Let FNA bind the texture to unit 0
                gd.Textures[0] = grp.Tex;

                // Upload instance data
                GCHandle pin = GCHandle.Alloc(grp.Data, GCHandleType.Pinned);
                try {
                    GL.glBindBuffer(GL.GL_ARRAY_BUFFER, _instanceVbo);
                    GL.glBufferSubData(GL.GL_ARRAY_BUFFER, IntPtr.Zero,
                        new IntPtr(count * InstanceStride), pin.AddrOfPinnedObject());
                } finally { pin.Free(); }

                GL.glDrawArraysInstanced(GL.GL_TRIANGLE_STRIP, 0, 4, count);
            }

            // Restore GL state
            GL.glBindVertexArray((uint)savedVao);
            GL.glUseProgram((uint)savedProg);
            GL.glBindBuffer(GL.GL_ARRAY_BUFFER, (uint)savedVbo);

            _groupCount = 0;
            _groupIndex.Clear();
        }
    }
}
