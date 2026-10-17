// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Mikael Järvinen
// More Players for Arizona Sunshine Remake - https://github.com/mikael-jarvinen/azs-remake-more-players
// Licensed under GPL-3.0 with the additional terms in ADDITIONAL-TERMS.md (attribution, permission to run with the game).

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Vertigo.Firebase;
using Vertigo.Networking;

namespace AZS1HD.MorePlayers;

/// <summary>
/// Calls FirebaseUserToNetworkMappingSystem methods that return a Vertigo.Firebase.User through an out parameter.
///
/// Do NOT call the generated wrappers (TryGetFirebaseUserForHost(host, out User) etc.): User is a 16-byte struct
/// (UserId string + DisplayName StaticString), but the wrapper hands the game a pointer to a single 8-byte local.
/// The game writes the whole struct there, which overwrites the stack and crashes the game (1.5.0 crashed every
/// co-op level start this way). Here the game writes into a properly sized boxed User instead.
/// </summary>
internal static class FirebaseUsers
{
    private static IntPtr forHostMethod, localUserMethod;
    private static bool resolved, resolveFailed;

    private static bool Resolve()
    {
        if (resolved) return !resolveFailed;
        resolved = true;
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(User).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(FirebaseUserToNetworkMappingSystem).TypeHandle);
            forHostMethod = MethodInfoPtr("NativeMethodInfoPtr_TryGetFirebaseUserForHost_");
            localUserMethod = MethodInfoPtr("NativeMethodInfoPtr_TryGetLocalUser_");
            resolveFailed = forHostMethod == IntPtr.Zero || Il2CppClassPointerStore<User>.NativeClassPtr == IntPtr.Zero;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Player accounts: could not resolve the game's account lookup: {e.Message}");
            resolveFailed = true;
        }
        if (resolveFailed) Plugin.Logger.LogWarning("Player accounts: lookup unavailable, save entries fall back to the game's defaults.");
        return !resolveFailed;
    }

    private static IntPtr MethodInfoPtr(string prefix)
    {
        foreach (var f in typeof(FirebaseUserToNetworkMappingSystem).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
            if (f.FieldType == typeof(IntPtr) && f.Name.StartsWith(prefix, StringComparison.Ordinal) && f.Name.EndsWith("_byref_User_0", StringComparison.Ordinal))
                return (IntPtr)f.GetValue(null);
        return IntPtr.Zero;
    }

    /// <summary>The account of a connected player (host or joiner).</summary>
    internal static bool TryGetForHost(FirebaseUserToNetworkMappingSystem map, Host host, out string userId, out string name)
    {
        userId = null; name = null;
        if (map == null || host == null || !Resolve()) return false;
        return Invoke(map, forHostMethod, IL2CPP.Il2CppObjectBaseToPtrNotNull(host), out userId, out name);
    }

    /// <summary>The local player's account.</summary>
    internal static bool TryGetLocal(FirebaseUserToNetworkMappingSystem map, out string userId, out string name)
    {
        userId = null; name = null;
        if (map == null || !Resolve() || localUserMethod == IntPtr.Zero) return false;
        return Invoke(map, localUserMethod, IntPtr.Zero, out userId, out name);
    }

    internal static unsafe bool Invoke(Il2CppObjectBase instance, IntPtr method, IntPtr firstArg, out string userId, out string name)
    {
        userId = null; name = null;
        // a zeroed boxed User; the game writes the struct into its data part
        var user = new User(IL2CPP.il2cpp_object_new(Il2CppClassPointerStore<User>.NativeClassPtr));
        IntPtr data = IL2CPP.il2cpp_object_unbox(IL2CPP.Il2CppObjectBaseToPtrNotNull(user));

        IntPtr* args = stackalloc IntPtr[2];
        int n = 0;
        if (firstArg != IntPtr.Zero) args[n++] = firstArg;
        args[n] = data;
        IntPtr exc = IntPtr.Zero;
        IntPtr ret = IL2CPP.il2cpp_runtime_invoke(method, IL2CPP.Il2CppObjectBaseToPtrNotNull(instance), (void**)args, ref exc);
        Il2CppException.RaiseExceptionIfNecessary(exc);
        if (ret == IntPtr.Zero || !*(bool*)IL2CPP.il2cpp_object_unbox(ret)) return false;

        userId = user._UserId_k__BackingField;   // plain field reads on the boxed copy
        try { name = user._DisplayName_k__BackingField?.ToString(); } catch { }
        return !string.IsNullOrEmpty(userId);
    }
}
