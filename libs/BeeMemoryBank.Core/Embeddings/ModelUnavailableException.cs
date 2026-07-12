using System;

namespace BeeMemoryBank.Core.Embeddings;

public class ModelUnavailableException : Exception
{
    public ModelUnavailableException()
    {
    }

    public ModelUnavailableException(string message)
        : base(message)
    {
    }

    public ModelUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
