﻿//  Copyright 2021 Google LLC. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using NtApiDotNet.Security;
using NtApiDotNet.Win32;
using NtApiDotNet.Win32.Rpc.Transport;
using NtApiDotNet.Win32.Security.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace NtApiDotNet.Net.Firewall
{
    /// <summary>
    /// Class to represent the firewall engine.
    /// </summary>
    public sealed class FirewallEngine : IDisposable, INtObjectSecurity
    {
        #region Private Members

        private readonly SafeFwpmEngineHandle _handle;

        private delegate Win32Error GetSecurityInfoByKey(SafeFwpmEngineHandle engineHandle,
            in Guid key,
            SecurityInformation securityInfo,
            IntPtr sidOwner,
            IntPtr sidGroup,
            IntPtr dacl,
            IntPtr sacl,
            out SafeFwpmMemoryBuffer securityDescriptor);

        private delegate Win32Error GetSecurityInfo(SafeFwpmEngineHandle engineHandle,
            SecurityInformation securityInfo,
            IntPtr sidOwner,
            IntPtr sidGroup,
            IntPtr dacl,
            IntPtr sacl,
            out SafeFwpmMemoryBuffer securityDescriptor);

        private delegate Win32Error CreateEnumHandleFunc(
            SafeFwpmEngineHandle engineHandle,
            SafeBuffer enumTemplate,
            out IntPtr enumHandle
        );

        private delegate Win32Error EnumObjectFunc(
            SafeFwpmEngineHandle engineHandle,
            IntPtr enumHandle,
            int numEntriesRequested,
            out SafeFwpmMemoryBuffer entries,
            out int numEntriesReturned
        );

        private delegate Win32Error DestroyEnumHandleFunc(
           SafeFwpmEngineHandle engineHandle,
           IntPtr enumHandle
        );

        private delegate Win32Error GetFirewallObjectByKey(
            SafeFwpmEngineHandle engineHandle,
            in Guid key,
            out SafeFwpmMemoryBuffer buffer);

        private NtResult<SecurityDescriptor> GetSecurity(SecurityInformation security_information, GetSecurityInfo func, bool throw_on_error)
        {
            security_information &= SecurityInformation.Owner | SecurityInformation.Group | SecurityInformation.Dacl | SecurityInformation.Sacl;

            var error = func(_handle, security_information,
                                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out SafeFwpmMemoryBuffer security_descriptor);
            if (error != Win32Error.SUCCESS)
            {
                return error.CreateResultFromDosError<SecurityDescriptor>(throw_on_error);
            }

            using (security_descriptor)
            {
                return SecurityDescriptor.Parse(security_descriptor, 
                   FirewallUtils.FirewallType, throw_on_error);
            }
        }

        private static NtResult<SecurityDescriptor> GetSecurityForKey(SafeFwpmEngineHandle engine_handle, SecurityInformation security_information, 
            Guid key, GetSecurityInfoByKey func, bool throw_on_error)
        {
            security_information &= SecurityInformation.Owner | SecurityInformation.Group | SecurityInformation.Dacl | SecurityInformation.Sacl;
            var error = func(engine_handle, key, security_information,
                                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out SafeFwpmMemoryBuffer security_descriptor);
            if (error != Win32Error.SUCCESS)
            {
                return error.CreateResultFromDosError<SecurityDescriptor>(throw_on_error);
            }

            using (security_descriptor)
            {
                return SecurityDescriptor.Parse(security_descriptor, FirewallUtils.FirewallType, throw_on_error);
            }
        }

        private FirewallFilter ProcessFilter(FWPM_FILTER0 filter)
        {
            return new FirewallFilter(filter, this, (i, t) => GetSecurityForKey(_handle, i, filter.filterKey, 
                FirewallNativeMethods.FwpmFilterGetSecurityInfoByKey0, t));
        }

        private FirewallLayer ProcessLayer(FWPM_LAYER0 layer)
        {
            return new FirewallLayer(layer, this, (i, t) => GetSecurityForKey(_handle, i, layer.layerKey,
                FirewallNativeMethods.FwpmLayerGetSecurityInfoByKey0, t));
        }

        private FirewallSubLayer ProcessSubLayer(FWPM_SUBLAYER0 sublayer)
        {
            return new FirewallSubLayer(sublayer, this, (i, t) => GetSecurityForKey(_handle, i, sublayer.subLayerKey,
                FirewallNativeMethods.FwpmSubLayerGetSecurityInfoByKey0, t));
        }

        private FirewallCallout ProcessCallout(FWPM_CALLOUT0 callout)
        {
            return new FirewallCallout(callout, this, (i, t) => GetSecurityForKey(_handle, i, callout.calloutKey,
                FirewallNativeMethods.FwpmCalloutGetSecurityInfoByKey0, t));
        }

        private FirewallProvider ProcessProvider(FWPM_PROVIDER0 provider)
        {
            return new FirewallProvider(provider, this, (i, t) => GetSecurityForKey(_handle, i, provider.providerKey,
                FirewallNativeMethods.FwpmProviderGetSecurityInfoByKey0, t));
        }

        private NtResult<List<T>> EnumerateFwObjects<T, U>(IFirewallEnumTemplate template, 
            Func<U, T> map_func, CreateEnumHandleFunc create_func, 
            EnumObjectFunc enum_func, DestroyEnumHandleFunc destroy_func, bool throw_on_error)
        {
            const int MAX_ENTRY = 1000;
            List<T> ret = new List<T>();
            using (var list = new DisposableList())
            {
                NtStatus status = create_func(_handle, template?.ToTemplateBuffer(list) ?? SafeHGlobalBuffer.Null, out IntPtr enum_handle).MapDosErrorToStatus();
                if (!status.IsSuccess())
                {
                    return status.CreateResultFromError<List<T>>(throw_on_error);
                }
                list.CallOnDispose(() => destroy_func(_handle, enum_handle));
                while (true)
                {
                    status = enum_func(_handle, enum_handle, MAX_ENTRY, out SafeFwpmMemoryBuffer entries, out int entry_count).MapDosErrorToStatus();
                    if (!status.IsSuccess())
                    {
                        return status.CreateResultFromError<List<T>>(throw_on_error);
                    }

                    using (entries)
                    {
                        if (entry_count > 0)
                        {
                            entries.Initialize<IntPtr>((uint)entry_count);
                            IntPtr[] ptrs = entries.ReadArray<IntPtr>(0, entry_count);
                            ret.AddRange(ptrs.Select(ptr => map_func((U)Marshal.PtrToStructure(ptr, typeof(U)))));
                        }

                        if (entry_count < MAX_ENTRY)
                        {
                            break;
                        }
                    }
                }
            }
            return ret.CreateResult();
        }

        private NtResult<T> GetFwObjectByKey<T, U>(Guid key, Func<U, T> map_func, GetFirewallObjectByKey get_func, bool throw_on_error)
        {
            return get_func(_handle, key, out SafeFwpmMemoryBuffer buffer).CreateWin32Result(throw_on_error, () =>
            {
                using (buffer)
                {
                    return map_func((U)Marshal.PtrToStructure(buffer.DangerousGetHandle(), typeof(U)));
                }
            });
        }

        #endregion

        #region Constructors
        private FirewallEngine(SafeFwpmEngineHandle handle)
        {
            _handle = handle;
        }
        #endregion

        #region Static Methods
        /// <summary>
        /// Open an instance of the engine.
        /// </summary>
        /// <param name="server_name">The server name for the firewall service.</param>
        /// <param name="authn_service">RPC authentication service. Use default or WinNT.</param>
        /// <param name="auth_identity">Optional authentication credentials.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The opened firewall engine.</returns>
        public static NtResult<FirewallEngine> Open(string server_name, RpcAuthenticationType authn_service, UserCredentials auth_identity, bool throw_on_error)
        {
            using (var list = new DisposableList())
            {
                var auth = auth_identity?.ToAuthIdentity(list);
                return FirewallNativeMethods.FwpmEngineOpen0(server_name, authn_service, auth, null,
                    out SafeFwpmEngineHandle handle).CreateWin32Result(throw_on_error, () => new FirewallEngine(handle));
            }
        }

        /// <summary>
        /// Open an instance of the engine.
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The opened firewall engine.</returns>
        public static NtResult<FirewallEngine> Open(bool throw_on_error)
        {
            return Open(null, RpcAuthenticationType.WinNT, null, throw_on_error);
        }

        /// <summary>
        /// Open an instance of the engine.
        /// </summary>
        /// <returns>The opened firewall engine.</returns>
        public static FirewallEngine Open()
        {
            return Open(true).Result;
        }
        #endregion

        #region Public Methods

        /// <summary>
        /// Get a layer by its key.
        /// </summary>
        /// <param name="key">The key of the layer.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The firewall layer.</returns>
        public NtResult<FirewallLayer> GetLayer(Guid key, bool throw_on_error)
        {
            Func<FWPM_LAYER0, FirewallLayer> f = ProcessLayer;
            return GetFwObjectByKey(key, f, FirewallNativeMethods.FwpmLayerGetByKey0, throw_on_error);
        }

        /// <summary>
        /// Get a layer by its key.
        /// </summary>
        /// <param name="key">The key of the layer.</param>
        /// <returns>The firewall layer.</returns>
        public FirewallLayer GetLayer(Guid key)
        {
            return GetLayer(key, true).Result;
        }

        /// <summary>
        /// Get a layer by its ID.
        /// </summary>
        /// <param name="id">The ID of the layer.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The firewall layer.</returns>
        public NtResult<FirewallLayer> GetLayer(int id, bool throw_on_error)
        {
            return FirewallNativeMethods.FwpmLayerGetById0(_handle, (ushort)id, out SafeFwpmMemoryBuffer buffer)
                .CreateWin32Result(throw_on_error, () =>
                {
                    using (buffer)
                    {
                        return ProcessLayer((FWPM_LAYER0)Marshal.PtrToStructure(buffer.DangerousGetHandle(), typeof(FWPM_LAYER0)));
                    }
                });
        }

        /// <summary>
        /// Get a layer by its ID.
        /// </summary>
        /// <param name="id">The ID of the layer.</param>
        /// <returns>The firewall layer.</returns>
        public FirewallLayer GetLayer(int id)
        {
            return GetLayer(id, true).Result;
        }

        /// <summary>
        /// Enumerate all layers.
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The list of layers.</returns>
        public NtResult<IEnumerable<FirewallLayer>> EnumerateLayers(bool throw_on_error)
        {
            Func<FWPM_LAYER0, FirewallLayer> f = ProcessLayer;
            return EnumerateFwObjects(null, f, FirewallNativeMethods.FwpmLayerCreateEnumHandle0,
                FirewallNativeMethods.FwpmLayerEnum0, FirewallNativeMethods.FwpmLayerDestroyEnumHandle0,
                throw_on_error).Map<IEnumerable<FirewallLayer>>(l => l.AsReadOnly());
        }

        /// <summary>
        /// Enumerate all layers.
        /// </summary>
        /// <returns>The list of layers.</returns>
        public IEnumerable<FirewallLayer> EnumerateLayers()
        {
            return EnumerateLayers(true).Result;
        }

        /// <summary>
        /// Get a sub-layer by its key.
        /// </summary>
        /// <param name="key">The key of the sub-layer.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The firewall sub-layer.</returns>
        public NtResult<FirewallSubLayer> GetSubLayer(Guid key, bool throw_on_error)
        {
            Func<FWPM_SUBLAYER0, FirewallSubLayer> f = ProcessSubLayer;
            return GetFwObjectByKey(key, f, FirewallNativeMethods.FwpmSubLayerGetByKey0, throw_on_error);
        }

        /// <summary>
        /// Get a sub-layer by its key.
        /// </summary>
        /// <param name="key">The key of the sub-layer.</param>
        /// <returns>The firewall sub-layer.</returns>
        public FirewallSubLayer GetSubLayer(Guid key)
        {
            return GetSubLayer(key, true).Result;
        }

        /// <summary>
        /// Enumerate all sub-layers.
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The list of sub-layers.</returns>
        public NtResult<IEnumerable<FirewallSubLayer>> EnumerateSubLayers(bool throw_on_error)
        {
            Func<FWPM_SUBLAYER0, FirewallSubLayer> f = ProcessSubLayer;

            return EnumerateFwObjects(null, f, FirewallNativeMethods.FwpmSubLayerCreateEnumHandle0,
                FirewallNativeMethods.FwpmSubLayerEnum0, FirewallNativeMethods.FwpmSubLayerDestroyEnumHandle0, 
                throw_on_error).Map<IEnumerable<FirewallSubLayer>>(l => l.AsReadOnly());
        }

        /// <summary>
        /// Enumerate all sub-layers.
        /// </summary>
        /// <returns>The list of sub-layers.</returns>
        public IEnumerable<FirewallSubLayer> EnumerateSubLayers()
        {
            return EnumerateSubLayers(true).Result;
        }

        /// <summary>
        /// Get a callout by its key.
        /// </summary>
        /// <param name="key">The key of the callout.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The firewall callout.</returns>
        public NtResult<FirewallCallout> GetCallout(Guid key, bool throw_on_error)
        {
            Func<FWPM_CALLOUT0, FirewallCallout> f = ProcessCallout;
            return GetFwObjectByKey(key, f, FirewallNativeMethods.FwpmCalloutGetByKey0, throw_on_error);
        }

        /// <summary>
        /// Get a callout by its key.
        /// </summary>
        /// <param name="key">The key of the callout.</param>
        /// <returns>The firewall callout.</returns>
        public FirewallCallout GetCallout(Guid key)
        {
            return GetCallout(key, true).Result;
        }

        /// <summary>
        /// Enumerate all callouts
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The list of callouts.</returns>
        public NtResult<IEnumerable<FirewallCallout>> EnumerateCallouts(bool throw_on_error)
        {
            Func<FWPM_CALLOUT0, FirewallCallout> f = ProcessCallout;

            return EnumerateFwObjects(null, f, FirewallNativeMethods.FwpmCalloutCreateEnumHandle0,
                FirewallNativeMethods.FwpmCalloutEnum0, FirewallNativeMethods.FwpmCalloutDestroyEnumHandle0, 
                throw_on_error).Map<IEnumerable<FirewallCallout>>(l => l.AsReadOnly());
        }

        /// <summary>
        /// Enumerate all callouts.
        /// </summary>
        /// <returns>The list of callouts.</returns>
        public IEnumerable<FirewallCallout> EnumerateCallouts()
        {
            return EnumerateCallouts(true).Result;
        }

        /// <summary>
        /// Get a filter by its key.
        /// </summary>
        /// <param name="key">The key of the filter.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The firewall filter.</returns>
        public NtResult<FirewallFilter> GetFilter(Guid key, bool throw_on_error)
        {
            Func<FWPM_FILTER0, FirewallFilter> f = ProcessFilter;
            return GetFwObjectByKey(key, f, FirewallNativeMethods.FwpmFilterGetByKey0, throw_on_error);
        }

        /// <summary>
        /// Get a filter by its key.
        /// </summary>
        /// <param name="key">The key of the filter.</param>
        /// <returns>The firewall filter.</returns>
        public FirewallFilter GetFilter(Guid key)
        {
            return GetFilter(key, true).Result;
        }

        /// <summary>
        /// Get a filter by its id.
        /// </summary>
        /// <param name="id">The ID of the filter.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The firewall filter.</returns>
        public NtResult<FirewallFilter> GetFilter(ulong id, bool throw_on_error)
        {
            return FirewallNativeMethods.FwpmFilterGetById0(_handle, id, out SafeFwpmMemoryBuffer buffer)
                .CreateWin32Result(throw_on_error, () =>
            {
                using (buffer)
                {
                    return ProcessFilter((FWPM_FILTER0)Marshal.PtrToStructure(buffer.DangerousGetHandle(), typeof(FWPM_FILTER0)));
                }
            });
        }

        /// <summary>
        /// Get a filter by its id.
        /// </summary>
        /// <param name="id">The ID of the filter.</param>
        /// <returns>The firewall filter.</returns>
        public FirewallFilter GetFilter(ulong id)
        {
            return GetFilter(id, true).Result;
        }

        /// <summary>
        /// Enumerate filters
        /// </summary>
        /// <param name="template">Specify a template for enumerating the filters.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The list of filters.</returns>
        public NtResult<IEnumerable<FirewallFilter>> EnumerateFilters(FirewallFilterEnumTemplate template, bool throw_on_error)
        {
            Func<FWPM_FILTER0, FirewallFilter> f = ProcessFilter;
            return EnumerateFwObjects(template, f, FirewallNativeMethods.FwpmFilterCreateEnumHandle0,
                FirewallNativeMethods.FwpmFilterEnum0, FirewallNativeMethods.FwpmFilterDestroyEnumHandle0,
                throw_on_error).Map<IEnumerable<FirewallFilter>>(l => l.AsReadOnly());
        }

        /// <summary>
        /// Enumerate filters
        /// </summary>
        /// <param name="template">Specify a template for enumerating the filters.</param>
        /// <returns>The list of filters.</returns>
        public IEnumerable<FirewallFilter> EnumerateFilters(FirewallFilterEnumTemplate template)
        {
            return EnumerateFilters(template, true).Result;
        }

        /// <summary>
        /// Enumerate all filters
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The list of filters.</returns>
        public NtResult<IEnumerable<FirewallFilter>> EnumerateFilters(bool throw_on_error)
        {
            return EnumerateFilters(null, throw_on_error);
        }

        /// <summary>
        /// Enumerate all filters.
        /// </summary>
        /// <returns>The list of filters.</returns>
        public IEnumerable<FirewallFilter> EnumerateFilters()
        {
            return EnumerateFilters(true).Result;
        }

        /// <summary>
        /// Get a provider by its key.
        /// </summary>
        /// <param name="key">The key of the provider.</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The firewall provider.</returns>
        public NtResult<FirewallProvider> GetProvider(Guid key, bool throw_on_error)
        {
            Func<FWPM_PROVIDER0, FirewallProvider> f = ProcessProvider;
            return GetFwObjectByKey(key, f, FirewallNativeMethods.FwpmProviderGetByKey0, throw_on_error);
        }

        /// <summary>
        /// Get a provider by its key.
        /// </summary>
        /// <param name="key">The key of the provider.</param>
        /// <returns>The firewall provider.</returns>
        public FirewallProvider GetProvider(Guid key)
        {
            return GetProvider(key, true).Result;
        }

        /// <summary>
        /// Enumerate all providers.
        /// </summary>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The list of providers.</returns>
        public NtResult<IEnumerable<FirewallProvider>> EnumerateProviders(bool throw_on_error)
        {
            Func<FWPM_PROVIDER0, FirewallProvider> f = ProcessProvider;
            return EnumerateFwObjects(null, f, FirewallNativeMethods.FwpmProviderCreateEnumHandle0,
                FirewallNativeMethods.FwpmProviderEnum0, FirewallNativeMethods.FwpmProviderDestroyEnumHandle0,
                throw_on_error).Map<IEnumerable<FirewallProvider>>(l => l.AsReadOnly());
        }

        /// <summary>
        /// Enumerate all providers.
        /// </summary>
        /// <returns>The list of providers.</returns>
        public IEnumerable<FirewallProvider> EnumerateProviders()
        {
            return EnumerateProviders(true).Result;
        }

        /// <summary>
        /// Get the security descriptor for the IKE SA database.
        /// </summary>
        /// <param name="security_information">What parts of the security descriptor to retrieve</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The security descriptor</returns>
        public NtResult<SecurityDescriptor> GetIkeSaDbSecurityDescriptor(SecurityInformation security_information, bool throw_on_error)
        {
            return GetSecurity(security_information, FirewallNativeMethods.IkeextSaDbGetSecurityInfo0, throw_on_error);
        }

        /// <summary>
        /// Get the security descriptor for the IKE SA database.
        /// </summary>
        /// <param name="security_information">What parts of the security descriptor to retrieve</param>
        /// <returns>The security descriptor</returns>
        public SecurityDescriptor GetIkeSaDbSecurityDescriptor(SecurityInformation security_information)
        {
            return GetIkeSaDbSecurityDescriptor(security_information, true).Result;
        }

        /// <summary>
        /// Get the security descriptor for the IKE SA database.
        /// </summary>
        /// <returns>The security descriptor</returns>
        public SecurityDescriptor GetIkeSaDbSecurityDescriptor()
        {
            return GetIkeSaDbSecurityDescriptor(SecurityInformation.Owner | SecurityInformation.Group | SecurityInformation.Dacl);
        }

        /// <summary>
        /// Dispose the engine.
        /// </summary>
        public void Dispose()
        {
            _handle?.Dispose();
        }
        #endregion

        #region INtObjectSecurity Implementation
        string INtObjectSecurity.ObjectName => "FwEngine";

        NtType INtObjectSecurity.NtType => FirewallUtils.FirewallType;

        SecurityDescriptor INtObjectSecurity.SecurityDescriptor => ((INtObjectSecurity)this).GetSecurityDescriptor(SecurityInformation.Owner | SecurityInformation.Group | SecurityInformation.Dacl);

        bool INtObjectSecurity.IsAccessMaskGranted(AccessMask access)
        {
            return true;
        }

        void INtObjectSecurity.SetSecurityDescriptor(SecurityDescriptor security_descriptor, SecurityInformation security_information)
        {
            throw new NotImplementedException();
        }

        NtStatus INtObjectSecurity.SetSecurityDescriptor(SecurityDescriptor security_descriptor, SecurityInformation security_information, bool throw_on_error)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Get the security descriptor specifying which parts to retrieve
        /// </summary>
        /// <param name="security_information">What parts of the security descriptor to retrieve</param>
        /// <returns>The security descriptor</returns>
        public SecurityDescriptor GetSecurityDescriptor(SecurityInformation security_information)
        {
            return ((INtObjectSecurity)this).GetSecurityDescriptor(security_information, true).Result;
        }

        /// <summary>
        /// Get the security descriptor specifying which parts to retrieve
        /// </summary>
        /// <param name="security_information">What parts of the security descriptor to retrieve</param>
        /// <param name="throw_on_error">True to throw on error.</param>
        /// <returns>The security descriptor</returns>
        public NtResult<SecurityDescriptor> GetSecurityDescriptor(SecurityInformation security_information, bool throw_on_error)
        {
            return GetSecurity(security_information, FirewallNativeMethods.FwpmEngineGetSecurityInfo0, throw_on_error);
        }
        #endregion
    }
}
