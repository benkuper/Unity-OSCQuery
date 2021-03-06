//
// IAvahiServiceResolver.cs
//
// Author:
//   Aaron Bockover <abockover@novell.com>
//
// Copyright (C) 2008 Novell, Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

#if UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX 

using System;
using NDesk.DBus;

namespace Mono.Zeroconf.Providers.AvahiDBus
{   
    public delegate void ServiceResolverFoundHandler (int @interface, Protocol protocol, string name, 
                string type, string domain, string host, Protocol aprotocol, string address, 
                ushort port, byte [][] txt, LookupResultFlags flags);
 
    public delegate void ServiceResolverErrorHandler (string error);
                   
    [Interface ("org.freedesktop.Avahi.ServiceResolver")]
    public interface IAvahiServiceResolver
    {
        event ServiceResolverFoundHandler Found;
        event ServiceResolverErrorHandler Failure;
        
        void Free ();
    }
}

#endif