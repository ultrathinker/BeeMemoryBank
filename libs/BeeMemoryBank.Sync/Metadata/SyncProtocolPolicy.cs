using System.Data.Common;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Sync.Metadata;

/// <summary>
/// The protocol this node declares and the peers it accepts (docs/design/step-a-spec.md 5.6): 3 until the node has applied
/// metadata_activation, 4 after. One instance per host (several in-process test nodes each have their own), read from
/// <c>tbl_metadata_activation</c> and flipped by <see cref="MarkActivated"/>.
///
/// <para>Activation is one way. Once seen it is cached for good. While it is not, the table is read again at most every
/// few seconds, so a row that arrives by another way than the applier (a join snapshot or a restore imports the table)
/// still switches the node: a node that holds an activation must never go on writing clear content. A read that FAILS (the database is
/// busy or still opening) says nothing and is not remembered: "not activated" is only ever remembered from a read that worked.</para>
/// </summary>
public sealed class SyncProtocolPolicy(IDbConnectionFactory connFactory) : ISyncProtocolPolicy
{
    private static readonly long RecheckMilliseconds = (long)TimeSpan.FromSeconds(5).TotalMilliseconds;

    private volatile bool _activated;
    private long _nextCheckAt;

    public bool SealsMetadata
    {
        get
        {
            if (_activated) return true;
            var now = Environment.TickCount64;
            if (now < Interlocked.Read(ref _nextCheckAt)) return false;
            switch (ReadActivated())
            {
                case true:
                    _activated = true;
                    break;
                case false:
                    Interlocked.Exchange(ref _nextCheckAt, now + RecheckMilliseconds);
                    break;
                case null:
                    // Could not read (a lock, a database that is opening): ask again at the next call instead of answering "no" for the next few seconds.
                    break;
            }
            return _activated;
        }
    }

    public int Declared => SealsMetadata ? EventClasses.SealedProtocol : SyncProtocolVersion.Current;

    public bool IsCompatiblePeer(int? declaredProtocol, IReadOnlyCollection<string>? capabilities)
    {
        if (!SealsMetadata) return SyncProtocolVersion.IsCompatiblePeer(declaredProtocol);
        // After activation: protocol 4, or a 2.6 node that has not applied the activation yet. It must be able to pull
        // the activation from here; refusing it would deadlock the switch. 2.6.x drops this window.
        return declaredProtocol >= EventClasses.SealedProtocol
               || (declaredProtocol == SyncProtocolVersion.Current && MetadataCapabilities.HasEnvelopeV1(capabilities));
    }

    public bool ShouldPullFrom(int? declaredProtocol) => !(declaredProtocol > EventClasses.SealedProtocol);

    public void MarkActivated() => _activated = true;

    public void RecheckNow() => Interlocked.Exchange(ref _nextCheckAt, 0);

    /// <summary>True: the activation row is there. False: it is not, or there is no table to hold it. Null: the read failed for another reason.</summary>
    private bool? ReadActivated()
    {
        try
        {
            using var conn = connFactory.CreateConnection();
            return conn.ExecuteScalar<long>("SELECT EXISTS (SELECT 1 FROM tbl_metadata_activation)") == 1;
        }
        catch (DbException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            // Before the migrations ran (a host resolving its services early) there is no table, and nothing can have
            // been activated in a database that has no table to record it.
            return false;
        }
        catch (DbException)
        {
            return null;
        }
    }
}

/// <summary>Where nothing is ever held (a blind host): asking for a drain does nothing.</summary>
public sealed class NoHeldEventDrain : IHeldEventDrain
{
    public void RequestDrain() { }
}
