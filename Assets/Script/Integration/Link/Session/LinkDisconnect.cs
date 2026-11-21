namespace YARG.Integration.Link.Session
{
    /// <summary>Why a joiner lost its host.</summary>
    public readonly struct LinkDisconnect
    {
        /// <summary>For people.</summary>
        public readonly string Reason;

        /// <summary>One of <see cref="ByeCodes"/>, or <c>null</c> if the connection just dropped.</summary>
        public readonly string Code;

        public LinkDisconnect(string reason, string code)
        {
            Reason = reason;
            Code = code;
        }

        /// <summary>
        /// Whether joining again could work without anyone changing anything: only when the connection just
        /// dropped. A host that ended its session on purpose comes back, if at all, with a new invite code.
        /// </summary>
        public bool CanRetry => Code is null;

        public override string ToString() => Code == null ? Reason : $"{Reason} ({Code})";
    }
}
