//  Copyright (c) .NET Foundation and Contributors
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace RestSharp.Extensions;

/// <summary>
/// Extension method overload!
/// </summary>
static class StreamExtensions {
    /// <summary>
    /// Read a stream into a byte array
    /// </summary>
    /// <param name="input">Stream to read</param>
    /// <param name="cancellationToken"></param>
    /// <returns>byte[]</returns>
    public static async Task<byte[]> ReadAsBytes(this Stream input, CancellationToken cancellationToken) {
        var buffer = new byte[16 * 1024];

        using var ms = new MemoryStream();

        int read;
#if NET
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
#else
        // On .NET Framework 4.8, Stream.ReadAsync(ct) on a ConnectStream backed by HttpWebRequest
        // does not reliably honour cancellation: the default CT registration calls stream.Close(),
        // which does NOT interrupt an in-progress BeginRead on the underlying socket.
        //
        // The only reliable escape is HttpWebRequest.Abort(), which sends a TCP RST and immediately
        // unblocks any pending BeginRead.
        //
        // IMPORTANT: the HttpWebRequest must be resolved HERE (eagerly, before the read loop
        // begins) rather than inside the CT callback. At this point the stream is open and
        // ConnectStream.m_Request is guaranteed to be set. Inside the callback the field may
        // already have been cleared, and the callback itself may run on a thread pool thread
        // while all application threads are blocked (so any logging infrastructure may be
        // unable to flush). The callback therefore does nothing but call Abort().
        var httpWebRequest = GetHttpWebRequest(input);
        using (cancellationToken.Register(() => {
            if (httpWebRequest != null)
                httpWebRequest.Abort();
            else
                input.Dispose();
        }))
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
#endif
            ms.Write(buffer, 0, read);

        return ms.ToArray();
    }

#if !NET
    /// <summary>
    /// Resolves the underlying <see cref="System.Net.HttpWebRequest"/> from the response stream
    /// on .NET Framework 4.8. Must be called BEFORE the read loop begins (i.e. eagerly), while
    /// the stream is open and <c>ConnectStream.m_Request</c> is guaranteed to be set.
    /// </summary>
    /// <remarks>
    /// On .NET Framework 4.8, <see cref="System.Net.Http.HttpClientHandler"/> wraps the raw
    /// <c>ConnectStream</c> inside a <c>WebExceptionWrapperStream</c>, which subclasses
    /// <c>DelegatingStream</c>. The wrapper has no own instance fields; the real stream lives in
    /// <c>DelegatingStream.innerStream</c> (private). That inner stream is a <c>ConnectStream</c>
    /// which holds the originating request in its private field <c>m_Request</c>.
    ///
    /// <c>GetField</c> with <c>NonPublic|Instance</c> only searches the concrete type's own
    /// declared fields — it does NOT walk base classes for <c>private</c> members. Use
    /// <see cref="GetFieldInHierarchy"/> to traverse the chain correctly.
    ///
    /// Calling <see cref="System.Net.HttpWebRequest.Abort"/> sends a TCP RST which immediately
    /// unblocks any pending <c>BeginRead</c>/<c>EndRead</c> on the socket.
    /// </remarks>
    static System.Net.HttpWebRequest GetHttpWebRequest(Stream stream) {
        try {
            // On .NET Framework 4.8 the stream chain returned by ReadAsStreamAsync is
            // (outermost → innermost):
            //   ReadOnlyStream           (StreamContent+ReadOnlyStream, a DelegatingStream)
            //     → WebExceptionWrapperStream  (HttpClientHandler nested type, DelegatingStream)
            //       → ConnectStream            (System.Net.ConnectStream)
            //           m_Request: HttpWebRequest  ← target
            //
            // Each wrapper stores the next level in DelegatingStream.innerStream (private).
            // Walk the chain until m_Request is found rather than hard-coding a fixed depth.
            var current = stream;
            while (current != null) {
                var mRequestField = GetFieldInHierarchy(current.GetType(), "m_Request");
                if (mRequestField?.GetValue(current) is System.Net.HttpWebRequest req)
                    return req;

                var innerField = GetFieldInHierarchy(current.GetType(), "innerStream");
                current = innerField?.GetValue(current) as Stream;
            }
        }
        catch {
            // Reflection failed — caller falls back to stream.Dispose().
        }
        return null;
    }

    static System.Reflection.FieldInfo GetFieldInHierarchy(Type type, string name) {
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        while (type != null && type != typeof(object)) {
            var f = type.GetField(name, Flags);
            if (f != null) return f;
            type = type.BaseType;
        }
        return null;
    }
#endif
}
