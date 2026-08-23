using Fmp.Core.Visualization.Rendering.Gpu;
using Xunit;

#nullable enable
namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

/// <summary>
/// Regression coverage for the GL-interface proc filter. The filter exists
/// because Skia's interface assembler probes EGL display functions
/// (eglQueryString / eglGetCurrentDisplay) and, when they resolve, calls
/// eglQueryString with the GLX context's display handle — garbage — crashing
/// in GrGLExtensions::init (mono/SkiaSharp#2350). Removing the filter
/// resurrects a SIGSEGV inside GRGlInterface.Create on Linux; these tests are
/// the guard so it is never "cleaned up".
/// </summary>
public sealed class GpuSkiaContextTest
{
    [Theory]
    [InlineData("eglQueryString", true)]
    [InlineData("eglGetCurrentDisplay", true)]
    [InlineData("eglGetCurrentContext", true)]
    [InlineData("eglGetError", true)]
    [InlineData("glGetString", false)]
    [InlineData("glGetStringi", false)]
    [InlineData("glClear", false)]
    [InlineData("glReadPixels", false)]
    [InlineData("GL_GLEXT_PROTOTYPES", false)]
    public void ShouldBlockGlProcName_MatchesEglPrefixOnly(string name, bool expected)
    {
        Assert.Equal(expected, GpuSkiaContext.ShouldBlockGlProcName(name));
    }

    [Fact]
    public void EveryGlFunctionSkiaNeeds_PassesTheFilter()
    {
        // The full kStandardFunctions core table must never be blocked; the
        // filter is ONLY for egl* display-extension probing.
        string[] coreProcs =
        {
            "glActiveTexture", "glAttachShader", "glBindAttribLocation",
            "glBindBuffer", "glBindFramebuffer", "glBindRenderbuffer",
            "glBindTexture", "glBlendColor", "glBlendEquation", "glBlendFunc",
            "glBufferData", "glBufferSubData", "glClear", "glClearColor",
            "glClearStencil", "glColorMask", "glCompileShader",
            "glCompressedTexImage2D", "glCompressedTexSubImage2D",
            "glCopyTexSubImage2D", "glCreateProgram", "glCreateShader",
            "glCullFace", "glDeleteBuffers", "glDeleteFramebuffers",
            "glDeleteProgram", "glDeleteRenderbuffers", "glDeleteShader",
            "glDeleteTextures", "glDepthMask", "glDisable",
            "glDisableVertexAttribArray", "glDrawArrays", "glDrawElements",
            "glEnable", "glEnableVertexAttribArray", "glFinish", "glFlush",
            "glFramebufferRenderbuffer", "glFramebufferTexture2D",
            "glGenBuffers", "glGenFramebuffers", "glGenRenderbuffers",
            "glGenTextures", "glGenerateMipmap", "glGetBufferSubData",
            "glGetFramebufferAttachmentParameteriv", "glGetIntegerv",
            "glGetProgramInfoLog", "glGetProgramiv", "glGetShaderInfoLog",
            "glGetShaderiv", "glGetString", "glGetStringi",
            "glGetUniformLocation", "glIsTexture", "glLinkProgram",
            "glPixelStorei", "glReadBuffer", "glReadPixels",
            "glRenderbufferStorage", "glRenderbufferStorageMultisample",
            "glScissor", "glShaderSource", "glStencilFunc",
            "glStencilFuncSeparate", "glStencilMask", "glStencilMaskSeparate",
            "glStencilOp", "glStencilOpSeparate", "glTexImage2D",
            "glTexParameteri", "glTexParameteriv", "glTexStorage2D",
            "glTexSubImage2D", "glUniform1f", "glUniform1fv", "glUniform1i",
            "glUniform1iv", "glUniform2f", "glUniform2fv", "glUniform2i",
            "glUniform2iv", "glUniform3f", "glUniform3fv", "glUniform3i",
            "glUniform3iv", "glUniform4f", "glUniform4fv", "glUniform4i",
            "glUniform4iv", "glUniformMatrix2fv", "glUniformMatrix3fv",
            "glUniformMatrix4fv", "glUseProgram", "glVertexAttrib1f",
            "glVertexAttrib2fv", "glVertexAttrib3fv", "glVertexAttrib4fv",
            "glVertexAttribPointer", "glViewport", "glBindVertexArray",
            "glGenVertexArrays", "glDeleteVertexArrays",
            "glVertexAttribDivisor", "glDeleteSync", "glFenceSync",
            "glWaitSync", "glMapBuffer", "glUnmapBuffer", "glGetError",
        };

        foreach (string proc in coreProcs)
            Assert.False(GpuSkiaContext.ShouldBlockGlProcName(proc), proc);
    }
}