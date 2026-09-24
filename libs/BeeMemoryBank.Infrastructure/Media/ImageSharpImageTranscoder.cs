// Moved to libs/BeeMemoryBank.Media/ImageSharpImageTranscoder.cs so the Mobile MAUI app
// can reference image transcoding without pulling in BeeMemoryBank.Infrastructure's
// ACME / mDNS / DPAPI surface. Consumers that need the transcoder now depend on
// BeeMemoryBank.Media directly; this file is intentionally empty.
namespace BeeMemoryBank.Infrastructure.Media;