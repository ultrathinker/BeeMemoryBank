using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Node;

/// <summary>
/// The node's own internal key, as the front holds it for the routes that are the shell's and not the web page's
/// (<c>/node/lan/*</c>, <c>/node/lock</c>). It is the front's counterpart of the Api's <c>InternalKeyValidator</c>: the
/// same header, the same constant-time comparison of the UTF-8 bytes (<see cref="CryptographicOperations.FixedTimeEquals"/>,
/// which answers false, not an exception, for a different length). It lives here because the front cannot reference the
/// Api; every route of the front that checks the key does it through this class, so there is exactly one comparer.
/// The key is kept as bytes and is never part of any text this class produces.
/// </summary>
internal sealed class NodeInternalKey
{
    public const string HeaderName = "X-Internal-Key";

    private readonly byte[] _key;

    public NodeInternalKey(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("An internal key is required.", nameof(key));
        _key = Encoding.UTF8.GetBytes(key);
    }

    /// <summary>True when the request carries exactly this key in <see cref="HeaderName"/>; false for none, a wrong one, or several.</summary>
    public bool IsPresentedBy(HttpRequest request)
    {
        var presented = Encoding.UTF8.GetBytes(request.Headers[HeaderName].ToString());
        return CryptographicOperations.FixedTimeEquals(presented, _key);
    }

    /// <summary>Puts this key on a request the front sends to its own Api child.</summary>
    public void AddTo(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation(HeaderName, Encoding.UTF8.GetString(_key));
    }
}
