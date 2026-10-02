namespace SaveSync
{
    public enum CloudError
    {
        None,

        /// <summary>There is no signed-in player to act for.</summary>
        NotSignedIn,

        /// <summary>The backend is missing configuration and cannot make requests.</summary>
        NotConfigured,

        /// <summary>The service could not be reached.</summary>
        Network,

        /// <summary>The request did not finish within the allowed time.</summary>
        Timeout,

        /// <summary>The request was cancelled by the caller.</summary>
        Cancelled,

        /// <summary>There is no save in the cloud.</summary>
        NotFound,

        /// <summary>The service answered and refused the request.</summary>
        Rejected,

        /// <summary>Anything else, including an exception thrown by the backend.</summary>
        Unexpected
    }

    /// <summary>
    /// Outcome of one backend request. Backends report failures through this type instead of
    /// throwing or returning null, so the caller always knows why a request did not succeed.
    /// </summary>
    public readonly struct CloudResult<T>
    {
        private CloudResult(bool isOk, T value, CloudError error, string message)
        {
            IsOk = isOk;
            Value = value;
            Error = error;
            Message = message;
        }

        public bool IsOk { get; }

        public T Value { get; }

        public CloudError Error { get; }

        public string Message { get; }

        public static CloudResult<T> Ok(T value)
        {
            return new CloudResult<T>(true, value, CloudError.None, null);
        }

        public static CloudResult<T> Fail(CloudError error, string message = null)
        {
            return new CloudResult<T>(false, default(T), error, message);
        }

        public override string ToString()
        {
            if (IsOk) return "Ok";
            return Message == null ? Error.ToString() : Error + ": " + Message;
        }
    }
}
