﻿using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace CsWhispers;

public unsafe static partial class Syscalls
{
    private const long Key = 0xdeadbeef;
    private static readonly List<SYSCALL_ENTRY> SyscallList = [];
    
    private static readonly byte[] X64IndirectSyscallStub =
    [
        0x49, 0x89, 0xCA,               			                // mov r10, rcx
        0xB8, 0x00, 0x00, 0x00, 0x00,    	              	        // mov eax, ssn
        0x49, 0xBB, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // movabs r11, address
        0x41, 0xFF, 0xE3 				       	                    // jmp r11
    ];

    private static readonly byte[] Wow64IndirectSyscallStub =
    [
        0xB8, 0x00, 0x00, 0x00, 0x00,               			    // mov eax, ssn
        0xBA, 0x00, 0x00, 0x00, 0x00,                               // mov edx, < wow64 transition >
        0xB9, 0x00, 0x00, 0x00, 0x00,                               // mov ecx, < address of [call edx] >
        0xFF, 0xE1                                                  // jmp ecx
    ];

    private unsafe static IntPtr PrepareJit(string func, byte* buffer, int length) 
    {
        IntPtr ptr;
        MethodInfo method = typeof(Syscalls).GetMethod(func, BindingFlags.Static | BindingFlags.NonPublic);
        RuntimeHelpers.PrepareMethod(method.MethodHandle);

        IntPtr pMethod = method.MethodHandle.GetFunctionPointer();
        if (Marshal.ReadByte(pMethod) != 0xe9)
        {
            ptr = pMethod;
        }
        else
        {
            Int32 offset = Marshal.ReadInt32(pMethod, 1);
            UInt64 addr = (UInt64)pMethod + (UInt64)offset;
            while (addr % 16 != 0) addr++;
            ptr = (IntPtr)addr;
        }

        for (int i = 0; i < length; i++)
        {
            *(byte*)IntPtr.Add(ptr, i) = *(byte*)(IntPtr.Add((IntPtr)buffer, i));
        }

        return ptr;
    }

    private unsafe static byte[] GetSyscallStub(string functionHash)
    {
        var ssn = GetSyscallNumber(functionHash);
        var syscall = SyscallList[ssn];


        byte[] stub;

        if (IntPtr.Size == 8)
        {
            stub = X64IndirectSyscallStub;
            stub[4] = (byte)ssn;

            var address = new byte[8];
            var delta = 0;

            while (true)
            {
                if (ssn - delta >= 0)
                    address = BitConverter.GetBytes((long)SyscallList[ssn - delta].Address + 18);
                if (*(ushort*)BitConverter.ToUInt64(address, 0) == 0x050F)
                    break;

                if (ssn + delta < SyscallList.Count)
                    address = BitConverter.GetBytes((long)SyscallList[ssn + delta].Address + 18);
                if (*(ushort*)BitConverter.ToUInt64(address, 0) == 0x050F)
                    break;

                // Todo: Handle case where we somehow get no stub(??)
                if (ssn - delta <= 0 && ssn + delta > SyscallList.Count)
                    break;

                delta++;
            }
            Buffer.BlockCopy(address, 0, stub, 10, address.Length);
        }
        else
        {
            stub = Wow64IndirectSyscallStub;
            stub[1] = (byte)ssn;

            var address = new byte[4];
            var delta = 0;

            // Get the Wow64Transition Address
            while (true)
            {
                if (ssn - delta >= 0)
                    address = BitConverter.GetBytes((uint)SyscallList[ssn - delta].Address);
                if (*(byte*)(BitConverter.ToUInt32(address, 0) + 0x5) == 0xBA &&
                    *(ushort*)(BitConverter.ToUInt32(address, 0) + 0xA) == 0xD2FF)
                    break;

                if (ssn + delta < SyscallList.Count)
                    address = BitConverter.GetBytes((uint)SyscallList[ssn + delta].Address);
                if (*(byte*)(BitConverter.ToUInt32(address, 0) + 0x5) == 0xBA &&
                    *(ushort*)(BitConverter.ToUInt32(address, 0) + 0xA) == 0xD2FF)
                    break;

                // Todo: Handle case where we somehow get no stub(??)
                if (ssn - delta <= 0 && ssn + delta > SyscallList.Count)
                    break;

                delta++;
            }
            var Wow64Value = BitConverter.GetBytes(*(uint*)(BitConverter.ToUInt32(address, 0) + 0x6));
            Buffer.BlockCopy(Wow64Value, 0, stub, 6, Wow64Value.Length);

            // Get the call edx
            delta = 0;
            while (true)
            {
                if (ssn - delta >= 0)
                    address = BitConverter.GetBytes((uint)SyscallList[ssn - delta].Address + 0xA);
                if (*(ushort*)BitConverter.ToUInt32(address, 0) == 0xD2FF)
                    break;

                if (ssn + delta < SyscallList.Count)
                    address = BitConverter.GetBytes((uint)SyscallList[ssn + delta].Address + 0xA);
                if (*(ushort*)BitConverter.ToUInt32(address, 0) == 0xD2FF)
                    break;

                if (ssn - delta <= 0 && ssn + delta > SyscallList.Count)
                    break;

                delta++;
            }
            Buffer.BlockCopy(address, 0, stub, 11, address.Length);
        }

        return stub;
    }

    private static int GetSyscallNumber(string functionHash)
    {

        if (SyscallList.Count == 0)
        {
            var hModule = Generic.GetLoadedModuleAddress("ntdll.dll");
            if (!PopulateSyscallList(hModule))
                return -1;
        }

        for (var i = 0; i < SyscallList.Count; i++)
            if (functionHash.Equals(SyscallList[i].Hash, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }

    private static bool PopulateSyscallList(IntPtr moduleBase)
    {
        var functionPtr = IntPtr.Zero;

        // Temp Entry to assign the attributes values before adding the element to the list
        SYSCALL_ENTRY Temp_Entry;

        // Traverse the PE header in memory
        var peHeader = Marshal.ReadInt32((IntPtr)(moduleBase.ToInt64() + 0x3C));
        var optHeader = moduleBase.ToInt64() + peHeader + 0x18;
        var magic = Marshal.ReadInt16((IntPtr)optHeader);
        var pExport = magic == 0x010b ? optHeader + 0x60 : optHeader + 0x70;

        var exportRva = Marshal.ReadInt32((IntPtr)pExport);
        var ordinalBase = Marshal.ReadInt32((IntPtr)(moduleBase.ToInt64() + exportRva + 0x10));
        var numberOfNames = Marshal.ReadInt32((IntPtr)(moduleBase.ToInt64() + exportRva + 0x18));
        var functionsRva = Marshal.ReadInt32((IntPtr)(moduleBase.ToInt64() + exportRva + 0x1C));
        var namesRva = Marshal.ReadInt32((IntPtr)(moduleBase.ToInt64() + exportRva + 0x20));
        var ordinalsRva = Marshal.ReadInt32((IntPtr)(moduleBase.ToInt64() + exportRva + 0x24));

        for (var i = 0; i < numberOfNames; i++)
        {
            var functionName = Marshal.PtrToStringAnsi((IntPtr)(moduleBase.ToInt64() +
                                                                Marshal.ReadInt32((IntPtr)(moduleBase.ToInt64() +
                                                                    namesRva + i * 4))));

            if (string.IsNullOrWhiteSpace(functionName))
                continue;

            // Check if is a syscall
            if (!functionName.StartsWith("Zw"))
                continue;

            var functionOrdinal = Marshal.ReadInt16((IntPtr)(moduleBase.ToInt64() + ordinalsRva + i * 2)) +
                                  ordinalBase;
            
            var functionRva =
                Marshal.ReadInt32((IntPtr)(moduleBase.ToInt64() + functionsRva +
                                           4 * (functionOrdinal - ordinalBase)));
            functionPtr = (IntPtr)((long)moduleBase + functionRva);

            Temp_Entry.Hash = HashSyscall(functionName);
            Temp_Entry.Address = functionPtr;

            // Add syscall to the list
            SyscallList.Add(Temp_Entry);
        }


        // Sort the list by address in ascending order.
        for (var i = 0; i < SyscallList.Count - 1; i++)
        {
            for (var j = 0; j < SyscallList.Count - i - 1; j++)
            {
                if (SyscallList[j].Address.ToInt64() > SyscallList[j + 1].Address.ToInt64())
                {
                    // Swap entries.
                    SYSCALL_ENTRY TempSwapEntry;

                    TempSwapEntry.Hash = SyscallList[j].Hash;
                    TempSwapEntry.Address = SyscallList[j].Address;

                    Temp_Entry.Hash = SyscallList[j + 1].Hash;
                    Temp_Entry.Address = SyscallList[j + 1].Address;

                    SyscallList[j] = Temp_Entry;
                    SyscallList[j + 1] = TempSwapEntry;
                }
            }
        }

        return true;
    }

    private static string HashSyscall(string functionName)
    {
        return GetApiHash(functionName, Key);
    }
    
    private static string GetApiHash(string name, long key)
    {
        var data = Encoding.UTF8.GetBytes(name.ToLower());
        var bytes = BitConverter.GetBytes(key);

        using var hmac = new HMACMD5(bytes);
        var bHash = hmac.ComputeHash(data);
        
        return BitConverter.ToString(bHash).Replace("-", "");
    }

    private struct SYSCALL_ENTRY
    {
        public string Hash;
        public IntPtr Address;
    }
}

