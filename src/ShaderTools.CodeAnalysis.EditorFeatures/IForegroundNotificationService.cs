﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Threading;
using ShaderTools.CodeAnalysis.Shared.TestHooks;

namespace ShaderTools.CodeAnalysis.Editor
{
    /// <summary>
    /// provide a way to call APIs from UI thread
    /// </summary>
    internal interface IForegroundNotificationService
    {
        void RegisterNotification(Action action, IAsyncToken asyncToken, CancellationToken cancellationToken = default(CancellationToken));

        void RegisterNotification(Action action, int delayInMS, IAsyncToken asyncToken, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// if action return true, the service will call it back again when it has time.
        /// </summary>
        void RegisterNotification(Func<bool> action, IAsyncToken asyncToken, CancellationToken cancellationToken = default(CancellationToken));

        void RegisterNotification(Func<bool> action, int delayInMS, IAsyncToken asyncToken, CancellationToken cancellationToken = default(CancellationToken));
    }
}
