using System;
using System.IO;
using BeeMemoryBank.Core.Embeddings;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Core.Tests;

public class OnnxEmbeddingGeneratorTests
{
    [Fact]
    public void Generate_WhenModelFileDoesNotExist_ThrowsModelUnavailableException()
    {
        // Arrange
        var generator = new OnnxEmbeddingGenerator("nonexistent_model_file.onnx");

        // Act
        var act = () => generator.Generate("hello world");

        // Assert
        var exception = act.Should().Throw<ModelUnavailableException>().Which;
        exception.InnerException.Should().BeOfType<FileNotFoundException>();
    }
}
