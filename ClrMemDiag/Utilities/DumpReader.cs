﻿//---------------------------------------------------------------------
//  This file is part of the CLR Managed Debugger (mdbg) Sample.
// 
//  Copyright (C) Microsoft Corporation.  All rights reserved.
//  
//  This provides a minidump reader for managed code.
//---------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.IO;
using System.Security.Permissions;
using System.Globalization;

// This provides a managed wrapper over the unmanaged dump-reading APIs in DbgHelp.dll.
// 
// ** This has several advantages:
// - type-safe wrappers
// - marshal minidump data-structures into the proper managed types (System.String,
// System.DateTime, System.Version, System.OperatingSystem, etc)
// 
// This does not validate aganst corrupted dumps. 
// 
// ** This is not a complete set of wrappers. 
// Other potentially interesting things to expose from the dump file:
// - the header. (Get flags, Version)
// - Exception stream (find last exception thrown)
// - 
// 
// ** Potential Performance improvements
// This was first prototyped in unmanaged C++, and was significantly faster there. 
// This is  not optimized for performance at all. It currently does not use unsafe C# and so has
// no pointers to structures and so has high costs from Marshal.PtrToStructure(typeof(T)) instead of
// just using T*. 
// This could probably be speed up signficantly (approaching the speed of the C++ prototype) by using unsafe C#. 
// 
// More data could be cached. A full dump may be 80 MB+, so caching extra data for faster indexing
// and lookup, especially for reading the memory stream.
// However, the app consuming the DumpReader is probably doing a lot of caching on its own, so
// extra caching in the dump reader may make microbenchmarks go faster, but just increase the
// working set and complexity of real clients.
// 
//     

namespace Microsoft.Diagnostics.Runtime.Utilities
{
    using System.Diagnostics;
    using System.Runtime.Serialization;
    using Microsoft.Diagnostics.Runtime.Desktop;
    using System.Threading;

    /// <summary>
    /// Immutable pointer into the dump file. Has associated size for runtime checking.
    /// </summary>
    unsafe struct DumpPointer
    {
        // This is dangerous because its lets you create a new arbitary dump pointer.
        static public DumpPointer DangerousMakeDumpPointer(IntPtr rawPointer, ulong size)
        {
            return new DumpPointer(rawPointer, size);
        }
        // Private ctor used to create new pointers from existing ones.
        DumpPointer(IntPtr rawPointer, ulong size)
        {
            m_pointer = rawPointer;
            m_size = size;
        }

        #region Transforms
        /// <summary>
        /// Returns a DumpPointer to the same memory, but associated with a smaller size.
        /// </summary>
        /// <param name="size">smaller size to shrink the pointer to.</param>
        /// <returns>new DumpPointer</returns>
        public DumpPointer Shrink(ulong size)
        {
            // Can't use this to grow.
            EnsureSizeRemaining(size);

            return new DumpPointer(m_pointer, m_size - size);
        }

        public DumpPointer Adjust(ulong delta64)
        {
            EnsureSizeRemaining(delta64);
            ulong ptr = unchecked((ulong)m_pointer.ToInt64()) + delta64;
            IntPtr pointer = new IntPtr(unchecked((long)ptr));

            return new DumpPointer(pointer, m_size - delta64);
        }
        #endregion // Transforms


        #region Data access
        /// <summary>
        /// Copy numberBytesToCopy from the DumpPointer into &amp;destinationBuffer[indexDestination].
        /// </summary>
        /// <param name="dest"></param>
        /// <param name="destinationBufferSizeInBytes"></param>
        /// <param name="numberBytesToCopy"></param>
        public void Copy(IntPtr dest, uint destinationBufferSizeInBytes, uint numberBytesToCopy)
        {
            // Esnure that both source and destination are large enough.
            EnsureSizeRemaining(numberBytesToCopy);
            if (numberBytesToCopy > destinationBufferSizeInBytes)
                throw new ArgumentException("Buffer too small");

            RawCopy(m_pointer, dest, numberBytesToCopy);
        }


        // All of the Marshal.Copy methods copy to arrays. We need to copy between IntPtrs. 
        // Provide a friendly wrapper over a raw pinvoke to RtlMoveMemory.
        // Note that we actually want a copy, but RtlCopyMemory is a macro and compiler intrinisic 
        // that we can't pinvoke to.
        static void RawCopy(IntPtr src, IntPtr dest, uint numBytes)
        {
            NativeMethods.RtlMoveMemory(dest, src, new IntPtr(numBytes));
        }


        internal ulong GetUlong()
        {
            return *(((ulong*)m_pointer.ToPointer()));
        }


        internal uint GetDword()
        {
            return *(((uint*)m_pointer.ToPointer()));
        }

        public void Copy(byte[] dest, int offset, int numberBytesToCopy)
        {
            // Esnure that both source and destination are large enough.
            EnsureSizeRemaining((uint)numberBytesToCopy);

            Marshal.Copy(m_pointer, dest, offset, numberBytesToCopy);
        }

        /// <summary>
        /// Copy raw bytes to buffer
        /// </summary>
        /// <param name="destinationBuffer">buffer to copy to.</param>
        /// <param name="sizeBytes">number of bytes to copy. Caller ensures the destinationBuffer
        /// is large enough</param>
        public void Copy(IntPtr destinationBuffer, uint sizeBytes)
        {
            EnsureSizeRemaining(sizeBytes);
            RawCopy(m_pointer, destinationBuffer, sizeBytes);
        }


        public int ReadInt32()
        {
            EnsureSizeRemaining(4);
            return Marshal.ReadInt32(m_pointer);
        }

        public long ReadInt64()
        {
            EnsureSizeRemaining(8);
            return Marshal.ReadInt64(m_pointer);
        }

        public UInt32 ReadUInt32()
        {
            EnsureSizeRemaining(4);
            return (UInt32)Marshal.ReadInt32(m_pointer);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StructUInt64
        {
            public UInt64 Value;
        }

        public UInt64 ReadUInt64()
        {
            EnsureSizeRemaining(8);
            return (UInt64)Marshal.ReadInt64(m_pointer);
        }

        public string ReadAsUnicodeString(int lengthChars)
        {
            int lengthBytes = lengthChars * 2;

            EnsureSizeRemaining((uint)lengthBytes);
            string s = Marshal.PtrToStringUni(m_pointer, lengthChars);
            return s;
        }


        public T PtrToStructure<T>(uint offset)
        {
            return this.Adjust(offset).PtrToStructure<T>();
        }


        public T PtrToStructureAdjustOffset<T>(ref uint offset)
        {
            T ret = this.Adjust(offset).PtrToStructure<T>();
            offset += (uint)Marshal.SizeOf(ret);
            return ret;
        }


        /// <summary>
        /// Marshal this into a managed structure, and do bounds checks.
        /// </summary>
        /// <typeparam name="T">Type of managed structure to marshal as</typeparam>
        /// <returns>a managed copy of the structure</returns>
        public T PtrToStructure<T>()
        {
            // Runtime check to ensure we have enough space in the minidump. This should
            // always be safe for well formed dumps.
            uint size = (uint)Marshal.SizeOf(typeof(T));
            EnsureSizeRemaining(size);

            T element = (T)Marshal.PtrToStructure(m_pointer, typeof(T));
            return element;
        }

        #endregion Data access

        void EnsureSizeRemaining(ulong requestedSize)
        {
            if (requestedSize > m_size)
                throw new ClrDiagnosticsException("The given crash dump is in an incorrect format.", ClrDiagnosticsException.HR.CrashDumpError);
        }

        // The actual raw pointer into the dump file (which is memory mapped into this process space.
        // We can directly dereference this to get data. 
        // We need to ensure that the pointer points into the dump file (and not stray memory).
        // 
        // Buffer safety is enforced through the type-system. The only way to get a DumpPointer is:
        // 1) From the mapped file. Pointer, Size provided by File-system APIs. This describes the
        //    largest possible region.
        // 2) From a Minidump stream. Pointer,Size are provided by MiniDumpReadDumpStream.

        // 3) From shrinking operations on existing dump-pointers. These operations return a
        //   DumpPointer that refers to a subset of the original. Since the original DumpPointer
        //   is in ranage, any subset must be in range too.
        //     Adjust(x) - moves pointer += x, shrinks size -= x. 
        //     Shrink(x) - size-= x.
        // 
        // All read operations in the dump-file then go through a DumpPointer, which validates
        // that there is enough size to fill the request.
        // All read operatiosn are still dangerous because there is no way that we can enforce that the data is 
        // what we expect it to be. However, since all operations are bounded, we should at worst
        // return corrupted data, but never read outside the dump-file.
        IntPtr m_pointer;

        // We explictly keep the size private because clients should not need to access it. Size
        // expectations are already described by the minidump format.
        ulong m_size;
    }

    /// <summary>
    /// Read contents of a minidump. 
    /// If we have a 32-bit dump, then there's an addressing collision possible.
    /// OS debugging code sign extends 32 bit wide addresses into 64 bit wide addresses.
    /// The CLR does not sign extend, thus you cannot round-trip target addresses exposed by this class.
    /// Currently we read these addresses once and don't hand them back, so it's not an issue.
    /// </summary>
    class DumpReader : IDisposable
    {
        protected internal static class DumpNative
        {
            /// <summary>
            /// Type of stream within the minidump.
            /// </summary>
            public enum MINIDUMP_STREAM_TYPE
            {
                UnusedStream = 0,
                ReservedStream0 = 1,
                ReservedStream1 = 2,
                ThreadListStream = 3,
                ModuleListStream = 4,
                MemoryListStream = 5,
                ExceptionStream = 6,
                SystemInfoStream = 7,
                ThreadExListStream = 8,
                Memory64ListStream = 9,
                CommentStreamA = 10,
                CommentStreamW = 11,
                HandleDataStream = 12,
                FunctionTableStream = 13,
                UnloadedModuleListStream = 14,
                MiscInfoStream = 15,
                MemoryInfoListStream = 16,
                ThreadInfoListStream = 17,
                LastReservedStream = 0xffff,
            }

            /// <summary>
            /// Remove the OS sign-extension from a target address.
            /// </summary>
            private static ulong ZeroExtendAddress(ulong addr)
            {
                // Since we only support debugging targets of the same bitness, we can presume that
                // the target dump process's bitness matches ours and strip the high bits.
                if (IntPtr.Size == 4)
                    return addr &= 0x00000000ffffffff;

                return addr;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct MINIDUMP_HEADER
            {
                public uint Singature;
                public uint Version;
                public uint NumberOfStreams;
                public uint StreamDirectoryRva;
                public uint CheckSum;
                public uint TimeDateStamp;
                public ulong Flags;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct MINIDUMP_DIRECTORY
            {
                public MINIDUMP_STREAM_TYPE StreamType;
                public uint DataSize;
                public uint Rva;
            }

            const uint MINIDUMP_SIGNATURE = 0x504d444d;
            const uint MINIDUMP_VERSION = 0xa793;
            const uint MiniDumpWithFullMemoryInfo = 0x0002;

            public static bool IsMiniDump(IntPtr pbase)
            {
                MINIDUMP_HEADER header = (MINIDUMP_HEADER)Marshal.PtrToStructure(pbase, typeof(MINIDUMP_HEADER));

                return ((header.Flags & MiniDumpWithFullMemoryInfo) == 0);
            }

            public static bool MiniDumpReadDumpStream(IntPtr pBase, MINIDUMP_STREAM_TYPE type, out IntPtr streamPointer, out uint cbStreamSize)
            {
                MINIDUMP_HEADER header = (MINIDUMP_HEADER)Marshal.PtrToStructure(pBase, typeof(MINIDUMP_HEADER));

                streamPointer = IntPtr.Zero;
                cbStreamSize = 0;

                // todo: throw dump format exception here:
                if (header.Singature != MINIDUMP_SIGNATURE || (header.Version & 0xffff) != MINIDUMP_VERSION)
                    return false;

                int sizeOfDirectory = Marshal.SizeOf(typeof(MINIDUMP_DIRECTORY));
                long dirs = pBase.ToInt64() + (int)header.StreamDirectoryRva;
                for (int i = 0; i < (int)header.NumberOfStreams; ++i)
                {
                    MINIDUMP_DIRECTORY dir = (MINIDUMP_DIRECTORY)Marshal.PtrToStructure(new IntPtr(dirs + i * sizeOfDirectory), typeof(MINIDUMP_DIRECTORY));
                    if (dir.StreamType != type)
                        continue;

                    streamPointer = new IntPtr(pBase.ToInt64() + (int)dir.Rva);
                    cbStreamSize = dir.DataSize;
                    return true;
                }

                return false;
            }

            #region RVA, etc
            // RVAs are offsets into the minidump.
            [StructLayout(LayoutKind.Sequential)]
            public struct RVA
            {
                public uint Value;

                public bool IsNull
                {
                    get { return Value == 0; }
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct RVA64
            {
                public ulong Value;
            }

            /// <summary>
            /// Describes a data stream within the minidump
            /// </summary>
            [StructLayout(LayoutKind.Sequential)]
            public struct MINIDUMP_LOCATION_DESCRIPTOR
            {
                /// <summary>
                /// Size of the stream in bytes.
                /// </summary>
                public uint DataSize;

                /// <summary>
                /// Offset (in bytes) from the start of the minidump to the data stream.
                /// </summary>
                public RVA Rva;

                /// <summary>
                /// True iff the data is missing.
                /// </summary>
                public bool IsNull
                {
                    get
                    {
                        return (DataSize == 0) || Rva.IsNull;
                    }
                }
            }
            /// <summary>
            /// Describes a data stream within the minidump
            /// </summary>
            [StructLayout(LayoutKind.Sequential)]
            public struct MINIDUMP_LOCATION_DESCRIPTOR64
            {
                /// <summary>
                /// Size of the stream in bytes.
                /// </summary>
                public ulong DataSize;

                /// <summary>
                /// Offset (in bytes) from the start of the minidump to the data stream.
                /// </summary>
                public RVA64 Rva;
            }

            /// <summary>
            /// Describes a range of memory in the target.
            /// </summary>
            [StructLayout(LayoutKind.Sequential)]
            public struct MINIDUMP_MEMORY_DESCRIPTOR
            {
                public const int SizeOf = 16;

                /// <summary>
                /// Starting Target address of the memory range.
                /// </summary>
                private ulong startofmemoryrange;
                public ulong StartOfMemoryRange
                {
                    get { return ZeroExtendAddress(startofmemoryrange); }
                }


                /// <summary>
                /// Location in minidump containing the memory corresponding to StartOfMemoryRage
                /// </summary>
                public MINIDUMP_LOCATION_DESCRIPTOR Memory;
            }

            /// <summary>
            /// Describes a range of memory in the target.
            /// </summary>
            /// <remarks>
            /// This is used for full-memory minidumps where
            /// all of the raw memory is laid out sequentially at the
            /// end of the dump.  There is no need for individual RVAs
            /// as the RVA is the base RVA plus the sum of the preceeding
            /// data blocks.
            /// </remarks>
            [StructLayout(LayoutKind.Sequential)]
            public struct MINIDUMP_MEMORY_DESCRIPTOR64
            {
                public const int SizeOf = 16;

                /// <summary>
                /// Starting Target address of the memory range.
                /// </summary>
                private ulong startofmemoryrange;
                public ulong StartOfMemoryRange
                {
                    get { return ZeroExtendAddress(startofmemoryrange); }
                }

                /// <summary>
                /// Size of memory in bytes.
                /// </summary>
                public ulong DataSize;
            }


            #endregion // Rva, MinidumpLocator, etc


            #region Minidump Exception

            // From ntxcapi_x.h, for example
            public const UInt32 EXCEPTION_MAXIMUM_PARAMETERS = 15;

            /// <summary>
            /// The struct that holds an EXCEPTION_RECORD
            /// </summary>
            [StructLayout(LayoutKind.Sequential)]
            public class MINIDUMP_EXCEPTION
            {
                public UInt32 ExceptionCode;
                public UInt32 ExceptionFlags;
                public UInt64 ExceptionRecord;

                private UInt64 exceptionaddress;
                public UInt64 ExceptionAddress
                {
                    get { return ZeroExtendAddress(exceptionaddress); }
                    set { exceptionaddress = value; }
                }

                public UInt32 NumberParameters;
                public UInt32 __unusedAlignment;
                public UInt64[] ExceptionInformation;

                public MINIDUMP_EXCEPTION()
                {
                    ExceptionInformation = new UInt64[EXCEPTION_MAXIMUM_PARAMETERS];
                }
            }


            /// <summary>
            /// The struct that holds contents of a dump's MINIDUMP_STREAM_TYPE.ExceptionStream
            /// which is a MINIDUMP_EXCEPTION_STREAM.
            /// </summary>
            [StructLayout(LayoutKind.Sequential)]
            public class MINIDUMP_EXCEPTION_STREAM
            {
                public UInt32 ThreadId;
                public UInt32 __alignment;
                public MINIDUMP_EXCEPTION ExceptionRecord;
                public MINIDUMP_LOCATION_DESCRIPTOR ThreadContext;

                public MINIDUMP_EXCEPTION_STREAM(DumpPointer dump)
                {
                    uint offset = 0;
                    ThreadId = dump.PtrToStructureAdjustOffset<UInt32>(ref offset);
                    __alignment = dump.PtrToStructureAdjustOffset<UInt32>(ref offset);

                    this.ExceptionRecord = new MINIDUMP_EXCEPTION();

                    ExceptionRecord.ExceptionCode = dump.PtrToStructureAdjustOffset<UInt32>(ref offset);
                    ExceptionRecord.ExceptionFlags = dump.PtrToStructureAdjustOffset<UInt32>(ref offset);
                    ExceptionRecord.ExceptionRecord = dump.PtrToStructureAdjustOffset<UInt64>(ref offset);
                    ExceptionRecord.ExceptionAddress = dump.PtrToStructureAdjustOffset<UInt64>(ref offset);
                    ExceptionRecord.NumberParameters = dump.PtrToStructureAdjustOffset<UInt32>(ref offset);
                    ExceptionRecord.__unusedAlignment = dump.PtrToStructureAdjustOffset<UInt32>(ref offset);

                    if (ExceptionRecord.ExceptionInformation.Length != EXCEPTION_MAXIMUM_PARAMETERS)
                    {
                        throw new ClrDiagnosticsException("Crash dump error: Expected to find " + EXCEPTION_MAXIMUM_PARAMETERS +
                            " exception params, but found " +
                            ExceptionRecord.ExceptionInformation.Length + " instead.", ClrDiagnosticsException.HR.CrashDumpError);
                    }

                    for (int i = 0; i < EXCEPTION_MAXIMUM_PARAMETERS; i++)
                    {
                        ExceptionRecord.ExceptionInformation[i] = dump.PtrToStructureAdjustOffset<UInt64>(ref offset);
                    }

                    ThreadContext.DataSize = dump.PtrToStructureAdjustOffset<UInt32>(ref offset);
                    ThreadContext.Rva.Value = dump.PtrToStructureAdjustOffset<UInt32>(ref offset);
                }
            }

            #endregion


            /// <summary>
            /// Describes system information about the system the dump was taken on.
            /// This is returned by the MINIDUMP_STREAM_TYPE.SystemInfoStream stream.
            /// </summary>
            [StructLayout(LayoutKind.Sequential)]
            public class MINIDUMP_SYSTEM_INFO
            {
                // There are existing managed types that represent some of these fields.
                // Provide both the raw imports, and the managed wrappers to make these easier to
                // consume from managed code.

                // These 3 fields are the same as in the SYSTEM_INFO structure from GetSystemInfo().
                // As of .NET 2.0, there is no existing managed object that represents these.
                public ProcessorArchitecture ProcessorArchitecture;
                public ushort ProcessorLevel; // only used for display purposes
                public ushort ProcessorRevision;


                public byte NumberOfProcessors;
                public byte ProductType;


                // These next 4 fields plus CSDVersionRva are the same as the OSVERSIONINFO structure from GetVersionEx().
                // This can be represented as a System.Version.
                public uint MajorVersion;
                public uint MinorVersion;
                public uint BuildNumber;

                // This enum is the same value as System.PlatformId.
                public System.PlatformID PlatformId;

                // RVA to a CSDVersion string in the string table.
                // This would be a string like "Service Pack 1".
                public RVA CSDVersionRva;


                // Remaining fields are not imported.



                //
                // Helper methods
                //

                public System.Version Version
                {
                    // System.Version is a managed abstraction on top of version numbers.
                    get
                    {
                        Version v = new Version((int)MajorVersion, (int)MinorVersion, (int)BuildNumber);
                        return v;
                    }
                }
            }

            #region Module


            [StructLayout(LayoutKind.Sequential)]
            internal struct VS_FIXEDFILEINFO
            {
                public uint dwSignature;            /* e.g. 0xfeef04bd */
                public uint dwStrucVersion;         /* e.g. 0x00000042 = "0.42" */
                public uint dwFileVersionMS;        /* e.g. 0x00030075 = "3.75" */
                public uint dwFileVersionLS;        /* e.g. 0x00000031 = "0.31" */
                public uint dwProductVersionMS;     /* e.g. 0x00030010 = "3.10" */
                public uint dwProductVersionLS;     /* e.g. 0x00000031 = "0.31" */
                public uint dwFileFlagsMask;        /* = 0x3F for version "0.42" */
                public uint dwFileFlags;            /* e.g. VFF_DEBUG | VFF_PRERELEASE */
                public uint dwFileOS;               /* e.g. VOS_DOS_WINDOWS16 */
                public uint dwFileType;             /* e.g. VFT_DRIVER */
                public uint dwFileSubtype;          /* e.g. VFT2_DRV_KEYBOARD */

                // Timestamps would be useful, but they're generally missing (0).
                public uint dwFileDateMS;           /* e.g. 0 */
                public uint dwFileDateLS;           /* e.g. 0 */
            }

            // Default Pack of 8 makes this struct 4 bytes too long
            // and so retrieving the last one will fail.
            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            public sealed class MINIDUMP_MODULE
            {
                /// <summary>
                /// Address that module is loaded within target.
                /// </summary>
                private ulong baseofimage;
                public ulong BaseOfImage
                {
                    get { return ZeroExtendAddress(baseofimage); }
                }

                /// <summary>
                /// Size of image within memory copied from IMAGE_OPTIONAL_HEADER.SizeOfImage.
                /// Note that this is usually different than the file size.
                /// </summary>
                public uint SizeOfImage;

                /// <summary>
                /// Checksum, copied from IMAGE_OPTIONAL_HEADER.CheckSum. May be 0 if not optional
                /// header is not available.
                /// </summary>
                public uint CheckSum;

                /// <summary>
                /// TimeStamp in Unix 32-bit time_t format. Copied from IMAGE_FILE_HEADER.TimeDateStamp
                /// </summary>
                public uint TimeDateStamp;

                /// <summary>
                /// RVA within minidump of the string containing the full path of the module.
                /// </summary>
                public RVA ModuleNameRva;

                internal VS_FIXEDFILEINFO VersionInfo;

                MINIDUMP_LOCATION_DESCRIPTOR CvRecord;


                MINIDUMP_LOCATION_DESCRIPTOR MiscRecord;


                ulong Reserved0;
                ulong Reserved1;

                /// <summary>
                /// Gets TimeDateStamp as a DateTime. This is based off a 32-bit value and will overflow in 2038.
                /// This is not the same as the timestamps on the file.
                /// </summary>
                public DateTime Timestamp
                {
                    get
                    {
                        // TimeDateStamp is a unix time_t structure (32-bit value).
                        // UNIX timestamps are in seconds since January 1, 1970 UTC. It is a 32-bit number
                        // Win32 FileTimes represents the number of 100-nanosecond intervals since January 1, 1601 UTC.
                        // We can create a System.DateTime from a FileTime.
                        // 
                        // See explanation here: http://blogs.msdn.com/oldnewthing/archive/2003/09/05/54806.aspx
                        // and here http://support.microsoft.com/default.aspx?scid=KB;en-us;q167296
                        long win32FileTime = 10000000 * (long)this.TimeDateStamp + 116444736000000000;
                        return DateTime.FromFileTimeUtc(win32FileTime);
                    }
                }
            }

            // Gotten from MiniDumpReadDumpStream via streamPointer
            // This is a var-args structure defined as:
            //   ULONG32 NumberOfModules;  
            //   MINIDUMP_MODULE Modules[];
            public class MINIDUMP_MODULE_LIST : MinidumpArray<MINIDUMP_MODULE>
            {
                internal MINIDUMP_MODULE_LIST(DumpPointer streamPointer)
                    : base(streamPointer, DumpNative.MINIDUMP_STREAM_TYPE.ModuleListStream)
                {
                }
            }

            #endregion // Module

            #region Threads
            /// <summary>
            /// Raw MINIDUMP_THREAD structure imported from DbgHelp.h
            /// </summary>
            [StructLayout(LayoutKind.Sequential)]
            public class MINIDUMP_THREAD
            {
                public uint ThreadId;

                // 0 if thread is not suspended.
                public uint SuspendCount;

                public uint PriorityClass;
                public uint Priority;

                // Target Address of Teb (Thread Environment block)
                private ulong teb;
                public ulong Teb
                {
                    get { return ZeroExtendAddress(teb); }
                }


                /// <summary>
                /// Describes the memory location of the thread's raw stack.
                /// </summary>
                public MINIDUMP_MEMORY_DESCRIPTOR Stack;

                public MINIDUMP_LOCATION_DESCRIPTOR ThreadContext;

                public virtual bool HasBackingStore()
                {
                    return false;
                }

                public virtual MINIDUMP_MEMORY_DESCRIPTOR BackingStore
                {
                    get
                    {
                        throw new MissingMemberException("MINIDUMP_THREAD has no backing store!");
                    }
                    set
                    {
                        throw new MissingMemberException("MINIDUMP_THREAD has no backing store!");
                    }
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public sealed class MINIDUMP_THREAD_EX : MINIDUMP_THREAD
            {
                override public bool HasBackingStore()
                {
                    return true;
                }

                public override MINIDUMP_MEMORY_DESCRIPTOR BackingStore
                {
                    get;
                    set;
                }
            }

            // Minidumps have a common variable length list structure for modules and threads implemented
            // as an array.
            // MINIDUMP_MODULE_LIST, MINIDUMP_THREAD_LIST, and MINIDUMP_THREAD_EX_LIST are the three streams
            // which use this implementation.
            // Others are similar in idea, such as MINIDUMP_THREAD_INFO_LIST, but are not the
            // same implementation and will not work with this class.  Thus, although this class
            // is generic, it's currently tightly bound to the implementation of those three streams.
            // This is a var-args structure defined as:
            //   ULONG32 NumberOfNodesInList;
            //   T ListNodes[];
            public class MinidumpArray<T>
            {

                protected MinidumpArray(DumpPointer streamPointer, DumpNative.MINIDUMP_STREAM_TYPE streamType)
                {
                    if ((streamType != DumpNative.MINIDUMP_STREAM_TYPE.ModuleListStream) &&
                        (streamType != DumpNative.MINIDUMP_STREAM_TYPE.ThreadListStream) &&
                        (streamType != DumpNative.MINIDUMP_STREAM_TYPE.ThreadExListStream))
                    {
                        throw new ClrDiagnosticsException("MinidumpArray does not support this stream type.", ClrDiagnosticsException.HR.CrashDumpError);
                    }
                    m_streamPointer = streamPointer;
                }
                DumpPointer m_streamPointer;

                public uint Count
                {
                    get
                    {
                        // Size is a 32-bit value at *(m_streamPointer + 0).
                        return m_streamPointer.ReadUInt32();
                    }
                }

                public T GetElement(uint idx)
                {
                    if (idx > Count)
                    {
                        // Since the callers here are internal, a request out of range means a
                        // corrupted dump file.
                        throw new ClrDiagnosticsException("Dump error: index " + idx + "is out of range.", ClrDiagnosticsException.HR.CrashDumpError);
                    }

                    // Although the Marshal.SizeOf(...) is not necessarily correct, it is nonetheless
                    // how we're going to pull the bytes back from the dump in PtrToStructure
                    // and so if it's wrong we have to fix it up anyhow.  This would have to be an incorrect
                    //  code change on our side anyhow; these are public native structs whose size is fixed.
                    // MINIDUMP_MODULE    : 0n108 bytes
                    // MINIDUMP_THREAD    : 0n48 bytes
                    // MINIDUMP_THREAD_EX : 0n64 bytes
                    const uint OffsetOfArray = 4;
                    uint offset = OffsetOfArray + (idx * (uint)Marshal.SizeOf(typeof(T)));

                    T element = m_streamPointer.PtrToStructure<T>(+offset);
                    return element;
                }
            }

            public interface IMinidumpThreadList
            {
                uint Count();
                MINIDUMP_THREAD GetElement(uint idx);
            }

            /// <summary>
            /// List of Threads in the minidump.
            /// </summary>
            public class MINIDUMP_THREAD_LIST<T> : MinidumpArray<T>, IMinidumpThreadList
                where T : MINIDUMP_THREAD
            {
                internal MINIDUMP_THREAD_LIST(DumpPointer streamPointer, DumpNative.MINIDUMP_STREAM_TYPE streamType)
                    : base(streamPointer, streamType)
                {

                    if ((streamType != DumpNative.MINIDUMP_STREAM_TYPE.ThreadListStream) &&
                        (streamType != DumpNative.MINIDUMP_STREAM_TYPE.ThreadExListStream))
                        throw new ClrDiagnosticsException("Only ThreadListStream and ThreadExListStream are supported.", ClrDiagnosticsException.HR.CrashDumpError);
                }

                // IMinidumpThreadList
                new public MINIDUMP_THREAD GetElement(uint idx)
                {
                    T t = base.GetElement(idx);
                    return (MINIDUMP_THREAD)t;
                }

                new public uint Count()
                {
                    return base.Count;
                }

            }

            #endregion // Threads

            #region Memory

            public class MinidumpMemoryChunk : IComparable<MinidumpMemoryChunk>
            {
                public UInt64 Size;
                public UInt64 TargetStartAddress;
                // TargetEndAddress is the first byte beyond the end of this chunk.
                public UInt64 TargetEndAddress;
                public UInt64 RVA;

                public int CompareTo(MinidumpMemoryChunk other)
                {
                    return TargetStartAddress.CompareTo(other.TargetStartAddress);
                }
            }

            // Class to represent chunks of memory from the target.
            // To add support for mapping files in and pretending they were part
            // of the dump, say for a MinidumpNormal when you can find the module
            // image on disk, you'd fall back on the image contents when 
            // ReadPartialMemory failed checking the chunks from the dump.
            // Practically speaking, that fallback could be in the
            // implementation of ICorDebugDataTarget.ReadVirtual.
            // Keep in mind this list presumes there are no overlapping chunks.
            public class MinidumpMemoryChunks
            {
                private UInt64 m_count;
                private MinidumpMemory64List m_memory64List;
                private MinidumpMemoryList m_memoryList;
                private MinidumpMemoryChunk[] m_chunks;
                private DumpPointer m_dumpStream;
                private MINIDUMP_STREAM_TYPE m_listType;

                public UInt64 Size(UInt64 i)
                {
                    return m_chunks[i].Size;
                }

                public UInt64 RVA(UInt64 i)
                {
                    return m_chunks[i].RVA;
                }

                public UInt64 StartAddress(UInt64 i)
                {
                    return m_chunks[i].TargetStartAddress;
                }

                public UInt64 EndAddress(UInt64 i)
                {
                    return m_chunks[i].TargetEndAddress;
                }

                public int GetChunkContainingAddress(ulong address)
                {
                    MinidumpMemoryChunk targetChunk = new MinidumpMemoryChunk();
                    targetChunk.TargetStartAddress = address;
                    int index = Array.BinarySearch(m_chunks, targetChunk);
                    if (index >= 0)
                    {
                        Debug.Assert(m_chunks[index].TargetStartAddress == address);
                        return index; // exact match will contain the address
                    }
                    else if (~index != 0)
                    {
                        int possibleIndex = Math.Min(m_chunks.Length, ~index) - 1;
                        if (m_chunks[possibleIndex].TargetStartAddress <= address &&
                            m_chunks[possibleIndex].TargetEndAddress > address)
                            return possibleIndex;
                    }
                    return -1;
                }

                public MinidumpMemoryChunks(DumpPointer rawStream, MINIDUMP_STREAM_TYPE type)
                {
                    m_count = 0;
                    m_memory64List = null;
                    m_memoryList = null;
                    m_listType = MINIDUMP_STREAM_TYPE.UnusedStream;

                    if ((type != MINIDUMP_STREAM_TYPE.MemoryListStream) &&
                        (type != MINIDUMP_STREAM_TYPE.Memory64ListStream))
                    {
                        throw new ClrDiagnosticsException("Type must be either MemoryListStream or Memory64ListStream", ClrDiagnosticsException.HR.CrashDumpError);
                    }

                    m_listType = type;
                    m_dumpStream = rawStream;
                    if (MINIDUMP_STREAM_TYPE.Memory64ListStream == type)
                    {
                        InitFromMemory64List();
                    }
                    else
                    {
                        InitFromMemoryList();
                    }

                }

                private void InitFromMemory64List()
                {
                    m_memory64List = new MinidumpMemory64List(m_dumpStream);

                    RVA64 currentRVA = m_memory64List.BaseRva;
                    ulong count = m_memory64List.Count;

                    // Initialize all chunks.
                    MINIDUMP_MEMORY_DESCRIPTOR64 tempMD;
                    List<MinidumpMemoryChunk> chunks = new List<MinidumpMemoryChunk>();
                    for (UInt64 i = 0; i < count; i++)
                    {
                        tempMD = m_memory64List.GetElement((uint)i);
                        MinidumpMemoryChunk chunk = new MinidumpMemoryChunk();
                        chunk.Size = tempMD.DataSize;
                        chunk.TargetStartAddress = tempMD.StartOfMemoryRange;
                        chunk.TargetEndAddress = tempMD.StartOfMemoryRange + tempMD.DataSize;
                        chunk.RVA = currentRVA.Value;
                        currentRVA.Value += tempMD.DataSize;
                        chunks.Add(chunk);
                    }

                    chunks.Sort();
                    SplitAndMergeChunks(chunks);
                    m_chunks = chunks.ToArray();
                    m_count = (ulong)chunks.Count;

                    ValidateChunks();

                }

                public void InitFromMemoryList()
                {
                    m_memoryList = new DumpNative.MinidumpMemoryList(m_dumpStream);
                    uint count = m_memoryList.Count;

                    MINIDUMP_MEMORY_DESCRIPTOR tempMD;
                    List<MinidumpMemoryChunk> chunks = new List<MinidumpMemoryChunk>();
                    for (UInt64 i = 0; i < count; i++)
                    {
                        MinidumpMemoryChunk chunk = new MinidumpMemoryChunk();
                        tempMD = m_memoryList.GetElement((uint)i);
                        chunk.Size = tempMD.Memory.DataSize;
                        chunk.TargetStartAddress = tempMD.StartOfMemoryRange;
                        chunk.TargetEndAddress = tempMD.StartOfMemoryRange + tempMD.Memory.DataSize;
                        chunk.RVA = tempMD.Memory.Rva.Value;
                        chunks.Add(chunk);
                    }

                    chunks.Sort();
                    SplitAndMergeChunks(chunks);
                    m_chunks = chunks.ToArray();
                    m_count = (ulong)chunks.Count;

                    ValidateChunks();
                }

                public UInt64 Count
                {
                    get
                    {
                        return m_count;
                    }
                }

                private void SplitAndMergeChunks(List<MinidumpMemoryChunk> chunks)
                {
                    for (int i = 1; i < chunks.Count; i++)
                    {
                        MinidumpMemoryChunk prevChunk = chunks[i - 1];
                        MinidumpMemoryChunk curChunk = chunks[i];

                        // we already sorted
                        Debug.Assert(prevChunk.TargetStartAddress <= curChunk.TargetStartAddress);

                        // there is some overlap
                        if (prevChunk.TargetEndAddress > curChunk.TargetStartAddress)
                        {
                            // the previous chunk completely covers this chunk rendering it useless
                            if (prevChunk.TargetEndAddress >= curChunk.TargetEndAddress)
                            {
                                chunks.RemoveAt(i);
                                i--;
                            }
                            // previous chunk partially covers this one so we will remove the front
                            // of this chunk and resort it if needed
                            else
                            {

                                ulong overlap = prevChunk.TargetEndAddress - curChunk.TargetStartAddress;
                                curChunk.TargetStartAddress += overlap;
                                curChunk.RVA += overlap;
                                curChunk.Size -= overlap;

                                // now that we changes the start address it might not be sorted anymore
                                // find the correct index
                                int newIndex = i;
                                for (; newIndex < chunks.Count - 1; newIndex++)
                                {
                                    if (curChunk.TargetStartAddress <= chunks[newIndex + 1].TargetStartAddress)
                                        break;
                                }
                                if (newIndex != i)
                                {
                                    chunks.RemoveAt(i);
                                    chunks.Insert(newIndex - 1, curChunk);
                                    i--;
                                }
                            }
                        }
                    }
                }

                private void ValidateChunks()
                {
                    for (UInt64 i = 0; i < m_count; i++)
                    {
                        if ((m_chunks[i].Size != m_chunks[i].TargetEndAddress - m_chunks[i].TargetStartAddress) ||
                            (m_chunks[i].TargetStartAddress > m_chunks[i].TargetEndAddress))
                        {
                            throw new ClrDiagnosticsException("Unexpected inconsistency error in dump memory chunk " + i
                                + " with target base address " + m_chunks[i].TargetStartAddress + ".", ClrDiagnosticsException.HR.CrashDumpError);
                        }

                        // If there's a next to compare to, and it's a MinidumpWithFullMemory, then we expect
                        // that the RVAs & addresses will all be sorted in the dump.
                        // MinidumpWithFullMemory stores things in a Memory64ListStream.
                        if (((i < m_count - 1) && (m_listType == MINIDUMP_STREAM_TYPE.Memory64ListStream)) &&
                            ((m_chunks[i].RVA >= m_chunks[i + 1].RVA) ||
                             (m_chunks[i].TargetEndAddress > m_chunks[i + 1].TargetStartAddress)))
                        {
                            throw new ClrDiagnosticsException("Unexpected relative addresses inconsistency between dump memory chunks "
                                + i + " and " + (i + 1) + ".", ClrDiagnosticsException.HR.CrashDumpError);
                        }

                        // Because we sorted and split/merged entries we can expect them to be increasing and non-overlapping
                        if ((i < m_count - 1) && (m_chunks[i].TargetEndAddress > m_chunks[i + 1].TargetStartAddress))
                        {
                            throw new ClrDiagnosticsException("Unexpected overlap between memory chunks", ClrDiagnosticsException.HR.CrashDumpError);
                        }

                    }

                }
            }


            // Usually about 300-500 elements long.
            // This does not have the right layout to use MinidumpArray
            public class MinidumpMemory64List
            {
                // Declaration of unmanaged structure is
                //   public ulong NumberOfMemoryRanges; // offset 0
                //   public RVA64 BaseRVA; // offset 8
                //   MINIDUMP_MEMORY_DESCRIPTOR64[]; // var-length embedded array
                public MinidumpMemory64List(DumpPointer streamPointer)
                {
                    m_streamPointer = streamPointer;
                }
                DumpPointer m_streamPointer;

                public UInt64 Count
                {
                    get
                    {
                        Int64 count = m_streamPointer.ReadInt64();
                        return (UInt64)count;
                    }
                }
                public RVA64 BaseRva
                {
                    get
                    {
                        RVA64 rva = m_streamPointer.PtrToStructure<RVA64>(8);
                        return rva;
                    }
                }

                public MINIDUMP_MEMORY_DESCRIPTOR64 GetElement(uint idx)
                {
                    // Embededded array starts at offset 16.
                    uint offset = 16 + idx * MINIDUMP_MEMORY_DESCRIPTOR64.SizeOf;
                    return m_streamPointer.PtrToStructure<MINIDUMP_MEMORY_DESCRIPTOR64>(offset);
                }
            }

            public class MinidumpMemoryList
            {
                // Declaration of unmanaged structure is
                //   public ulong NumberOfMemoryRanges; // offset 0
                //   MINIDUMP_MEMORY_DESCRIPTOR[]; // var-length embedded array
                public MinidumpMemoryList(DumpPointer streamPointer)
                {
                    m_streamPointer = streamPointer;
                }
                DumpPointer m_streamPointer;

                public UInt32 Count
                {
                    get
                    {
                        long count = m_streamPointer.ReadInt32();
                        return (UInt32)count;
                    }
                }

                public MINIDUMP_MEMORY_DESCRIPTOR GetElement(uint idx)
                {
                    // Embededded array starts at offset 4.
                    uint offset = 4 + idx * MINIDUMP_MEMORY_DESCRIPTOR.SizeOf;
                    return m_streamPointer.PtrToStructure<MINIDUMP_MEMORY_DESCRIPTOR>(offset);
                }
            }

            // If the dump doesn't have memory contents, we can try to load the file
            // off disk and report as if memory contents were present.
            // Run through loader to simplify getting the in-memory layout correct, rather than using a FileStream
            // and playing around with trying to mimic the loader.
            public class LoadedFileMemoryLookups
            {
                private Dictionary<String, SafeLoadLibraryHandle> m_files;

                public LoadedFileMemoryLookups()
                {
                    m_files = new Dictionary<String, SafeLoadLibraryHandle>();
                }

                unsafe public void GetBytes(String fileName, UInt64 offset, IntPtr destination, uint bytesRequested, ref uint bytesWritten)
                {
                    bytesWritten = 0;
                    IntPtr file;
                    // Did we already attempt to load this file?
                    // Only makes one attempt to load a file.
                    if (!m_files.ContainsKey(fileName))
                    {
                        //TODO: real code here to get the relocations right without loading would be nice, but
                        // that's a significant amount of code - especially if you intend to compensate for linker bugs.
                        // The easiest way to accomplish this would be to build on top of dbgeng.dll which already
                        // does all this for you.  Then you can also use dbghelp & get all your module and symbol
                        // loading for free, with full integration with symbol servers.
                        //
                        // In the meantime, this is a cheap hack that doesn't actually exec any code from the module
                        // we load.  Mdbg should be done loading modules for itself, so if we happen to load some
                        // module in common with mdbg we'll be fine because this call will be second.
                        // Lifetime issues could be important if we load some module here and do not release it back
                        // to the OS before mdbg loads it subsequently to execute it.
                        // Also, note that rebasing will not be correct, so raw assembly addresses will be relative
                        // to the base address of the module in mdbg's process, not the base address in the dump.
                        file = NativeMethods.LoadLibraryEx(fileName, 0, NativeMethods.LoadLibraryFlags.DontResolveDllReferences);
                        m_files[fileName] = new SafeLoadLibraryHandle(file);
                        //TODO: Attempted file load order is NOT guaranteed, so the uncertainty will make output order non-deterministic.
                        // Find/create an appropriate global verbosity setting.
                        /*
                        if (file.Equals(IntPtr.Zero))
                        {
                            String warning = "DataTarget: failed to load \"" + fileName + "\"";
                            CommandBase.Write(MDbgOutputConstants.StdOutput, warning, 0, warning.Length);
                        }
                        else 
                        {
                            CommandBase.WriteOutput("DataTarget: loaded \"" + fileName + "\"");
                        }
                        */
                    }
                    else
                    {
                        file = m_files[fileName].BaseAddress;
                    }

                    // Did we actually succeed loading this file?
                    if (!file.Equals(IntPtr.Zero))
                    {
                        file = new IntPtr((byte*)file.ToPointer() + offset);
                        InternalGetBytes(file, destination, bytesRequested, ref bytesWritten);
                    }
                }

                unsafe private void InternalGetBytes(IntPtr src, IntPtr dest, uint bytesRequested, ref uint bytesWritten)
                {
                    // Do the raw copy.
                    byte* pSrc = (byte*)src.ToPointer();
                    byte* pDest = (byte*)dest.ToPointer();
                    for (bytesWritten = 0; bytesWritten < bytesRequested; bytesWritten++)
                    {
                        pDest[bytesWritten] = pSrc[bytesWritten];
                    }
                }
            }

            #endregion // Memory
        } // End native methods

        #region Utility

        // Get a DumpPointer from a MINIDUMP_LOCATION_DESCRIPTOR
        protected internal DumpPointer TranslateDescriptor(DumpNative.MINIDUMP_LOCATION_DESCRIPTOR location)
        {
            // A Location has both an RVA and Size. If we just TranslateRVA, then that would be a
            // DumpPointer associated with a larger size (to the end of the dump-file). 
            DumpPointer p = TranslateRVA(location.Rva);
            p.Shrink(location.DataSize);
            return p;
        }

        /// <summary>
        /// Translates from an RVA to Dump Pointer. 
        /// </summary>
        /// <param name="rva">RVA within the dump</param>
        /// <returns>DumpPointer representing RVA.</returns>
        protected internal DumpPointer TranslateRVA(UInt64 rva)
        {
            return m_base.Adjust(rva);
        }

        /// <summary>
        /// Translates from an RVA to Dump Pointer. 
        /// </summary>
        /// <param name="rva">RVA within the dump</param>
        /// <returns>DumpPointer representing RVA.</returns>
        protected internal DumpPointer TranslateRVA(DumpNative.RVA rva)
        {
            return m_base.Adjust(rva.Value);
        }

        /// <summary>
        /// Translates from an RVA to Dump Pointer. 
        /// </summary>
        /// <param name="rva">RVA within the dump</param>
        /// <returns>DumpPointer representing RVA.</returns>
        protected internal DumpPointer TranslateRVA(DumpNative.RVA64 rva)
        {
            return m_base.Adjust(rva.Value);
        }


        /// <summary>
        /// Gets a MINIDUMP_STRING at the given RVA as an System.String.
        /// </summary>
        /// <param name="rva">RVA of MINIDUMP_STRING</param>
        /// <returns>System.String representing contents of MINIDUMP_STRING at the given RVA</returns>
        protected internal String GetString(DumpNative.RVA rva)
        {
            DumpPointer p = TranslateRVA(rva);
            return GetString(p);
        }

        /// <summary>
        /// Gets a MINIDUMP_STRING at the given DumpPointer as an System.String.
        /// </summary>
        /// <param name="ptr">DumpPointer to a MINIDUMP_STRING</param>
        /// <returns>System.String representing contents of MINIDUMP_STRING at the given location
        /// in the dump</returns>
        protected internal String GetString(DumpPointer ptr)
        {
            EnsureValid();

            // Minidump string is defined as:
            // typedef struct _MINIDUMP_STRING {
            //   ULONG32 Length;         // Length in bytes of the string
            //    WCHAR   Buffer [0];     // Variable size buffer
            // } MINIDUMP_STRING, *PMINIDUMP_STRING;
            int lengthBytes = ptr.ReadInt32();

            ptr = ptr.Adjust(4); // move past the Length field

            int lengthChars = lengthBytes / 2;
            string s = ptr.ReadAsUnicodeString(lengthChars);
            return s;
        }

        #endregion // Utility


        #region Read Memory
        public bool VirtualQuery(ulong addr, out VirtualQueryData data)
        {
            uint min = 0, max = (uint)m_memoryChunks.Count - 1;

            while (min <= max)
            {
                uint mid = (max + min) / 2;

                ulong targetStartAddress = m_memoryChunks.StartAddress(mid);

                if (addr < targetStartAddress)
                {
                    max = mid - 1;
                }
                else
                {
                    ulong targetEndAddress = m_memoryChunks.EndAddress(mid);
                    if (targetEndAddress < addr)
                    {
                        min = mid + 1;
                    }
                    else
                    {
                        data = new VirtualQueryData(targetStartAddress, m_memoryChunks.Size(mid));
                        return true;
                    }
                }
            }

            data = new VirtualQueryData();
            return false;
        }


        public IEnumerable<VirtualQueryData> EnumerateMemoryRanges(ulong startAddress, ulong endAddress)
        {
            for (ulong i = 0; i < m_memoryChunks.Count; i++)
            {
                ulong targetStartAddress = m_memoryChunks.StartAddress(i);
                ulong targetEndAddress = m_memoryChunks.EndAddress(i);

                if (targetEndAddress < startAddress)
                    continue;
                else if (endAddress < targetStartAddress)
                    continue;

                ulong size = m_memoryChunks.Size(i);
                yield return new VirtualQueryData(targetStartAddress, size);
            }
        }

        /// <summary>
        /// Read memory from the dump file and return results in newly allocated buffer
        /// </summary>
        /// <param name="targetAddress">target address in dump to read length bytes from</param>
        /// <param name="length">number of bytes to read</param>
        /// <returns>newly allocated byte array containing dump memory</returns>
        /// <remarks>All memory requested must be readable or it throws.</remarks>
        public byte[] ReadMemory(ulong targetAddress, int length)
        {
            byte[] buffer = new byte[length];
            ReadMemory(targetAddress, buffer,  length);
            return buffer;
        }

        /// <summary>
        /// Read memory from the dump file and copy into the buffer
        /// </summary>
        /// <param name="targetAddress">target address in dump to read buffer.Length bytets from</param>
        /// <param name="buffer">destination buffer to copy target memory to.</param>
        /// <remarks>All memory requested must be readable or it throws.</remarks>
        public void ReadMemory(ulong targetAddress, byte[] buffer, int cbRequestSize)
        {
            GCHandle h = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                ReadMemory(targetAddress, h.AddrOfPinnedObject(), (uint)cbRequestSize);
            }
            finally
            {
                h.Free();
            }
        }

        /// <summary>
        /// Read memory from target and copy it to the local buffer pointed to by
        /// destinationBuffer. Throw if any portion of the requested memory is unavailable.
        /// </summary>
        /// <param name="targetRequestStart">target address in dump file to copy
        /// destinationBufferSizeInBytes bytes from. </param>
        /// <param name="destinationBuffer">pointer to copy the memory to.</param>
        /// <param name="destinationBufferSizeInBytes">size of the destinationBuffer in bytes.</param>
        public void ReadMemory(ulong targetRequestStart, IntPtr destinationBuffer, uint destinationBufferSizeInBytes)
        {
            uint bytesRead = ReadPartialMemory(targetRequestStart, destinationBuffer, destinationBufferSizeInBytes);
            if (bytesRead != destinationBufferSizeInBytes)
            {
                throw new ClrDiagnosticsException(
                    String.Format(CultureInfo.CurrentUICulture,
                    "Memory missing at {0}. Could only read {1} bytes of {2} total bytes requested.",
                    targetRequestStart.ToString("x"), bytesRead, destinationBufferSizeInBytes), ClrDiagnosticsException.HR.CrashDumpError);
            }
        }

        /*

        /// <summary>
        /// Read memory from the dump file and copy into the buffer
        /// </summary>
        /// <param name="targetAddress">target address in dump to read buffer.Length bytets from</param>
        /// <param name="buffer">destination buffer to copy target memory to.</param>
        /// <remarks>All memory requested must be readable or it throws.</remarks>
        public uint ReadPartialMemory(ulong targetAddress, byte[] buffer)
        {
            GCHandle h = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                uint cbRequestSize = (uint)buffer.Length;
                return ReadPartialMemory(targetAddress, h.AddrOfPinnedObject(), cbRequestSize);
            }
            finally
            {
                h.Free();
            }
        }
        
         */
        /// <summary>
        /// Read memory from target and copy it to the local buffer pointed to by destinationBuffer.
        /// 
        /// </summary>
        /// <param name="targetRequestStart">target address in dump file to copy
        /// destinationBufferSizeInBytes bytes from. </param>
        /// <param name="destinationBuffer">pointer to copy the memory to.</param>
        /// <param name="destinationBufferSizeInBytes">size of the destinationBuffer in bytes.</param>
        /// <returns>Number of contiguous bytes successfuly copied into the destination buffer.</returns>
        public virtual uint ReadPartialMemory(ulong targetRequestStart, IntPtr destinationBuffer, uint destinationBufferSizeInBytes)
        {
            uint bytesRead = ReadPartialMemoryInternal(targetRequestStart,
                                                        destinationBuffer,
                                                        destinationBufferSizeInBytes,
                                                        0);
            return bytesRead;
        }



        internal ulong ReadPointerUnsafe(ulong addr)
        {
            int chunkIndex = m_memoryChunks.GetChunkContainingAddress(addr);
            if (chunkIndex == -1)
                return 0;

            DumpPointer chunk = this.TranslateRVA(m_memoryChunks.RVA((uint)chunkIndex));
            ulong offset = addr - m_memoryChunks.StartAddress((uint)chunkIndex);

            if (IntPtr.Size == 4)
                return chunk.Adjust(offset).GetDword();

            return chunk.Adjust(offset).GetUlong();
        }



        internal uint ReadDwordUnsafe(ulong addr)
        {
            int chunkIndex = m_memoryChunks.GetChunkContainingAddress(addr);
            if (chunkIndex == -1)
                return 0;

            DumpPointer chunk = this.TranslateRVA(m_memoryChunks.RVA((uint)chunkIndex));
            ulong offset = addr - m_memoryChunks.StartAddress((uint)chunkIndex);
            return chunk.Adjust(offset).GetDword();
        }


        public virtual int ReadPartialMemory(ulong targetRequestStart, byte[] destinationBuffer, int bytesRequested)
        {
            EnsureValid();

            if (bytesRequested <= 0)
                return 0;

            if (bytesRequested > destinationBuffer.Length)
                bytesRequested = destinationBuffer.Length;

            int bytesRead = 0;
            do
            {
                int chunkIndex = m_memoryChunks.GetChunkContainingAddress(targetRequestStart + (uint)bytesRead);
                if (chunkIndex == -1)
                    break;

                DumpPointer pointerCurrentChunk = this.TranslateRVA(m_memoryChunks.RVA((uint)chunkIndex));
                ulong startAddr = targetRequestStart + (uint)bytesRead - m_memoryChunks.StartAddress((uint)chunkIndex);
                ulong bytesAvailable = m_memoryChunks.Size((uint)chunkIndex) - startAddr;

                Debug.Assert(bytesRequested >= bytesRead);
                int bytesToCopy = bytesRequested - bytesRead;
                if (bytesAvailable < (uint)bytesToCopy)
                    bytesToCopy = (int)bytesAvailable;

                Debug.Assert(bytesToCopy > 0);
                if (bytesToCopy == 0)
                    break;

                pointerCurrentChunk.Adjust(startAddr).Copy(destinationBuffer, bytesRead, bytesToCopy);
                bytesRead += bytesToCopy;

            } while (bytesRead < bytesRequested);

            return bytesRead;
        }

        public virtual void ReadMemory(AsyncMemoryReadResult result)
        {
            EnsureValid();

            byte[] buffer = new byte[result.BytesRequested];
            bool acquired = false;
            try
            {
                // Don't actually do anything if we failed to acquire the read lock
                acquired = AcquireReadLock();
                if (acquired)
                    result.BytesRead = ReadPartialMemory(result.Address, buffer, buffer.Length);
                else
                    result.BytesRead = 0;
            }
            finally
            {
                if (acquired)
                    ReleaseReadLock();
            }

            result.Result = buffer;
            result.Complete.Set();
        }

#pragma warning disable 0420
        volatile bool m_disposing;
        volatile int m_lock = 0;
        bool AcquireReadLock()
        {
            int result = 0;
            int value = 0;
            do
            {
                value = m_lock;
                if (m_disposing || value < 0)
                    return false;

                result = Interlocked.CompareExchange(ref m_lock, value+1, value);
            } while (result != value);

            return true;
        }

        void ReleaseReadLock()
        {
            Interlocked.Decrement(ref m_lock);
        }

        bool AcquireWriteLock()
        {
            int result = 0;
            result = Interlocked.CompareExchange(ref m_lock, -1, 0);
            while (result != 0)
            {
                Thread.Sleep(50);
                result = Interlocked.CompareExchange(ref m_lock, -1, 0);
            }

            return true;
        }

        void ReleaseWriteLock()
        {
            Interlocked.Increment(ref m_lock);
        }


        // Since a MemoryListStream makes no guarantees that there aren't duplicate, overlapping, or wholly contained
        // memory regions, we need to handle that.  For the purposes of this code, we presume all memory regions
        // in the dump that cover a given VA have the correct (duplicate) contents.
        protected uint ReadPartialMemoryInternal(ulong targetRequestStart,
                                                IntPtr destinationBuffer,
                                                uint destinationBufferSizeInBytes,
                                                uint startIndex)
        {
            EnsureValid();

            if (destinationBufferSizeInBytes == 0)
                return 0;
            uint bytesRead = 0;
            do
            {
                int chunkIndex = m_memoryChunks.GetChunkContainingAddress(targetRequestStart + bytesRead);
                if (chunkIndex == -1)
                    break;

                DumpPointer pointerCurrentChunk = this.TranslateRVA(m_memoryChunks.RVA((uint)chunkIndex));
                uint idxStart = (uint)(targetRequestStart + bytesRead - m_memoryChunks.StartAddress((uint)chunkIndex));
                uint bytesAvailable = (uint)m_memoryChunks.Size((uint)chunkIndex) - idxStart;
                uint bytesNeeded = destinationBufferSizeInBytes - bytesRead;
                uint bytesToCopy = Math.Min(bytesAvailable, bytesNeeded);

                Debug.Assert(bytesToCopy > 0);
                if (bytesToCopy == 0)
                    break;

                IntPtr dest = new IntPtr(destinationBuffer.ToInt64() + bytesRead);
                uint destSize = destinationBufferSizeInBytes - bytesRead;
                pointerCurrentChunk.Adjust(idxStart).Copy(dest, destSize, bytesToCopy);
                bytesRead += bytesToCopy;

            } while (bytesRead < destinationBufferSizeInBytes);

            return bytesRead;
        }



        // Caching the chunks avoids the cost of Marshal.PtrToStructure on every single element in the memory list.
        // Empirically, this cache provides huge performance improvements for read memory.
        // This cache could be completey removed if we used unsafe C# and just had direct pointers
        // into the mapped dump file.
        protected DumpNative.MinidumpMemoryChunks m_memoryChunks;
        // The backup lookup method for memory that's not in the dump is to try and load the memory
        // from the same file on disk.
        protected DumpNative.LoadedFileMemoryLookups m_mappedFileMemory;

        #endregion // Read Memory

        /// <summary>
        /// ToString override. 
        /// </summary>
        /// <returns>string description of the DumpReader.</returns>
        public override string ToString()
        {
            if (m_file == null)
            {
                return "Empty";
            }
            return m_file.Name;
        }


        public bool IsMinidump { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="path">filename to open dump file</param>
        public DumpReader(string path)
        {
            m_file = File.OpenRead(path);
            long length = m_file.Length;

            // The dump file may be many megabytes large, so we don't want to
            // read it all at once. Instead, doing a mapping.
            m_fileMapping = NativeMethods.CreateFileMapping(m_file.SafeFileHandle, IntPtr.Zero, NativeMethods.PageProtection.Readonly, 0, 0, null);

            if (m_fileMapping.IsInvalid)
            {
                int error = Marshal.GetHRForLastWin32Error();
                Marshal.ThrowExceptionForHR(error, new IntPtr(-1));
            }

            m_View = NativeMethods.MapViewOfFile(m_fileMapping, NativeMethods.FILE_MAP_READ, 0, 0, IntPtr.Zero);
            if (m_View.IsInvalid)
            {
                int error = Marshal.GetHRForLastWin32Error();
                Marshal.ThrowExceptionForHR(error, new IntPtr(-1));
            }

            m_base = DumpPointer.DangerousMakeDumpPointer(m_View.BaseAddress, (ulong)length);

            //
            // Cache stuff
            //

            DumpPointer pStream;

            // System info.            
            pStream = GetStream(DumpNative.MINIDUMP_STREAM_TYPE.SystemInfoStream);
            m_info = pStream.PtrToStructure<DumpNative.MINIDUMP_SYSTEM_INFO>();

            // Memory64ListStream is present in MinidumpWithFullMemory.
            if (TryGetStream(DumpNative.MINIDUMP_STREAM_TYPE.Memory64ListStream, out pStream))
            {
                m_memoryChunks = new DumpNative.MinidumpMemoryChunks(pStream, DumpNative.MINIDUMP_STREAM_TYPE.Memory64ListStream);
            }
            else
            {
                // MiniDumpNormal doesn't have a Memory64ListStream, it has a MemoryListStream.
                pStream = GetStream(DumpNative.MINIDUMP_STREAM_TYPE.MemoryListStream);
                m_memoryChunks = new DumpNative.MinidumpMemoryChunks(pStream, DumpNative.MINIDUMP_STREAM_TYPE.MemoryListStream);
            }

            m_mappedFileMemory = new DumpNative.LoadedFileMemoryLookups();
            IsMinidump = DumpNative.IsMiniDump(m_View.BaseAddress);
        }


        /// <summary>
        /// Dispose method.
        /// </summary>
        public void Dispose()
        {
            // Clear any cached objects.
            m_disposing = true;
            AcquireWriteLock();

            m_info = null;
            m_memoryChunks = null;
            m_mappedFileMemory = null;

            // All resources are backed by safe-handles, so we don't need a finalizer.
            if (m_fileMapping != null)
                m_fileMapping.Close();

            if (m_View != null)
                m_View.Close();

            if (m_file != null)
                m_file.Dispose();
        }


        // Helper to ensure the object is not yet disposed.
        void EnsureValid()
        {
            if (m_file == null)
            {
                throw new ObjectDisposedException("DumpReader");
            }
        }

        FileStream m_file;
        SafeWin32Handle m_fileMapping;
        SafeMapViewHandle m_View;

        // DumpPointer (raw pointer that's aware of remaining buffer size) for start of minidump. 
        // This is useful for computing RVAs.
        DumpPointer m_base;

        // Cached info
        DumpNative.MINIDUMP_SYSTEM_INFO m_info;



        /// <summary>
        /// Get a DumpPointer for the given stream. That can then be used to further decode the stream.
        /// </summary>
        /// <param name="type">type of stream to lookup</param>
        /// <returns>DumpPointer refering into the stream. </returns>
        DumpPointer GetStream(DumpNative.MINIDUMP_STREAM_TYPE type)
        {
            DumpPointer stream;
            if (!TryGetStream(type, out stream))
            {
                throw new ClrDiagnosticsException("Dump does not contain a " + type + " stream.", ClrDiagnosticsException.HR.CrashDumpError);
            }
            return stream;
        }

        /// <summary>
        /// Get a DumpPointer for the given stream. That can then be used to further decode the stream.
        /// </summary>
        /// <param name="type">type of stream to lookup</param>
        /// <param name="stream">DumpPointer refering into the stream. </param>
        /// <returns>True if stream was succesfully retrived</returns>
        bool TryGetStream(DumpNative.MINIDUMP_STREAM_TYPE type, out DumpPointer stream)
        {
            EnsureValid();

            IntPtr pStream;
            uint cbStreamSize;

            bool fOk = DumpNative.MiniDumpReadDumpStream(m_View.BaseAddress, type, out pStream, out cbStreamSize);

            if ((!fOk) || (IntPtr.Zero == pStream) || (cbStreamSize < 1))
            {
                stream = default(DumpPointer);
                return false;
            }

            stream = DumpPointer.DangerousMakeDumpPointer(pStream, cbStreamSize);
            return true;
        }


        #region Information


        /// <summary>
        /// Version numbers of OS that this dump was taken on.
        /// </summary>
        public Version Version
        {
            get
            {
                return m_info.Version;
            }
        }

        /// <summary>
        /// Operating system that the dump was taken on.
        /// </summary>
        public OperatingSystem OSVersion
        {
            get
            {
                PlatformID id = m_info.PlatformId;
                Version v = this.Version;

                // Ideally, we'd include the CSDVersion string, but the public ctor for
                // OperatingSystem doesn't allow that. So we have a OSVersionString property that
                // will include both OS and CSDVersion. If we can ever fix this, then adjust 
                // the OSVersionString property accordingly.
                OperatingSystem os = new OperatingSystem(id, v);
                return os;
            }
        }

        /// <summary>
        /// Friendly helper to get full OS version string (including CSDVersion) that the dump was taken on.
        /// </summary>
        /// <remarks>This is really just to compensate that public OperatingSystem's ctor doesn't let us
        /// add the service pack string, so we need a special helper for that.</remarks>
        public string OSVersionString
        {
            get
            {
                EnsureValid();
                string s = GetString(m_info.CSDVersionRva);
                return OSVersion.ToString() + " " + s;
            }
        }

        /// <summary>
        /// The processor architecture that this dump was taken on.
        /// </summary>
        public ProcessorArchitecture ProcessorArchitecture
        {
            get
            {
                EnsureValid();
                return m_info.ProcessorArchitecture;
            }
        }

        #endregion // Information

        #region Threads

        /// <summary>
        /// Get the thread for the given thread Id.
        /// </summary>
        /// <param name="threadId">thread Id to lookup.</param>
        /// <returns>a DumpThread object representing a thread in the dump whose thread id matches
        /// the requested id.</returns>
        public DumpThread GetThread(int threadId)
        {
            EnsureValid();
            var raw = GetRawThread(threadId);
            if (raw == null)
                return null;

            return new DumpThread(this, raw);
        }

        // Helper to get the thread list in the dump.
        DumpNative.IMinidumpThreadList GetThreadList()
        {
            EnsureValid();

            DumpPointer pStream;

            DumpNative.MINIDUMP_STREAM_TYPE streamType;
            DumpNative.IMinidumpThreadList list;
            try
            {
                // On x86 and X64, we have the ThreadListStream.  On IA64, we have the ThreadExListStream.
                streamType = DumpNative.MINIDUMP_STREAM_TYPE.ThreadListStream;
                pStream = GetStream(streamType);
                list = new DumpNative.MINIDUMP_THREAD_LIST<DumpNative.MINIDUMP_THREAD>(pStream, streamType);
            }
            catch (ClrDiagnosticsException)
            {
                streamType = DumpNative.MINIDUMP_STREAM_TYPE.ThreadExListStream;
                pStream = GetStream(streamType);
                list = new DumpNative.MINIDUMP_THREAD_LIST<DumpNative.MINIDUMP_THREAD_EX>(pStream, streamType);
            }

            return list;
        }


        /// <summary>
        /// Enumerate all the native threads in the dump
        /// </summary>
        /// <returns>an enumerate of DumpThread objects</returns>
        public IEnumerable<DumpThread> EnumerateThreads()
        {
            DumpNative.IMinidumpThreadList list = GetThreadList();
            uint num = list.Count();

            for (uint i = 0; i < num; i++)
            {
                DumpNative.MINIDUMP_THREAD rawThread = list.GetElement(i);
                yield return new DumpThread(this, rawThread);
            }
        }

        // Internal helper to get the raw Minidump thread object.
        // Throws if thread is not found.
        DumpNative.MINIDUMP_THREAD GetRawThread(int threadId)
        {
            DumpNative.IMinidumpThreadList list = GetThreadList();
            uint num = list.Count();

            for (uint i = 0; i < num; i++)
            {
                DumpNative.MINIDUMP_THREAD thread = list.GetElement(i);
                if (threadId == thread.ThreadId)
                {
                    return thread;
                }
            }

            return null;
        }

        /*
        /// <summary>
        /// Retrieve a thread context at the given location
        /// </summary>
        /// <param name="threadId">OS thread ID of the thread</param>
        /// <returns>a native context object representing the thread context</returns>
        internal NativeContext GetThreadContext(DumpReader.NativeMethods.MINIDUMP_LOCATION_DESCRIPTOR loc)
        {
            NativeContext context = ContextAllocator.GenerateContext();
            GetThreadContext(loc, context);
            return context;
        }

        /// <summary>
        /// Retrieve a thread context at the given location
        /// </summary>
        /// <param name="threadId">OS thread ID of the thread</param>
        /// <returns>a native context object representing the thread context</returns>
        internal void GetThreadContext(DumpReader.NativeMethods.MINIDUMP_LOCATION_DESCRIPTOR loc, NativeContext context)
        {
            using (IContextDirectAccessor w = context.OpenForDirectAccess())
            {
                GetThreadContext(loc, w.RawBuffer, w.Size);
            }
        }
        */
        internal void GetThreadContext(DumpReader.DumpNative.MINIDUMP_LOCATION_DESCRIPTOR loc, IntPtr buffer, int sizeBufferBytes)
        {
            if (loc.IsNull)
            {
                throw new ClrDiagnosticsException("Context not present", ClrDiagnosticsException.HR.CrashDumpError);
            }

            DumpPointer pContext = TranslateDescriptor(loc);
            int sizeContext = (int)loc.DataSize;

            if (sizeBufferBytes < sizeContext)
            {
                // Context size doesn't match
                throw new ClrDiagnosticsException("Context size mismatch. Expected = 0x" + sizeBufferBytes.ToString("x") + ", Size in dump = 0x" + sizeContext.ToString("x"), ClrDiagnosticsException.HR.CrashDumpError);
            }

            // Now copy from dump into buffer. 
            pContext.Copy(buffer, (uint)sizeContext);
        }

        #endregion // Threads

        #region Modules

        // Internal helper to get the list of modules
        DumpNative.MINIDUMP_MODULE_LIST GetModuleList()
        {
            EnsureValid();
            DumpPointer pStream = GetStream(DumpNative.MINIDUMP_STREAM_TYPE.ModuleListStream);
            DumpNative.MINIDUMP_MODULE_LIST list = new DumpNative.MINIDUMP_MODULE_LIST(pStream);

            return list;
        }

        DumpNative.MINIDUMP_EXCEPTION_STREAM GetExceptionStream()
        {
            DumpPointer pStream = GetStream(DumpNative.MINIDUMP_STREAM_TYPE.ExceptionStream);
            return new DumpNative.MINIDUMP_EXCEPTION_STREAM(pStream);
        }

        /// <summary>
        /// Check on whether there's an exception stream in the dump
        /// </summary>
        /// <returns> true iff there is a MINIDUMP_EXCEPTION_STREAM in the dump. </returns>
        public bool IsExceptionStream()
        {
            bool ret = true;
            try
            {
                GetExceptionStream();
            }
            catch (ClrDiagnosticsException)
            {
                ret = false;
            }

            return ret;
        }


        /// <summary>
        /// Return the TID from the exception stream.
        /// </summary>
        /// <returns> The TID from the exception stream. </returns>
        public UInt32 ExceptionStreamThreadId()
        {
            DumpNative.MINIDUMP_EXCEPTION_STREAM es = GetExceptionStream();
            return es.ThreadId;
        }

        //todo
        /*
        public NativeContext ExceptionStreamThreadContext()
        {
            NativeMethods.MINIDUMP_EXCEPTION_STREAM es = GetExceptionStream();
            return GetThreadContext(es.ThreadContext);
        }
         */

        /// <summary>
        /// Lookup the first module in the target with a matching. 
        /// </summary>
        /// <param name="nameModule">The name can either be a matching full name, or just shortname</param>
        /// <returns>The first DumpModule that has a matching name. </returns>
        public DumpModule LookupModule(string nameModule)
        {
            DumpNative.MINIDUMP_MODULE_LIST list = GetModuleList();
            uint num = list.Count;

            for (uint i = 0; i < num; i++)
            {
                DumpNative.MINIDUMP_MODULE module = list.GetElement(i);
                DumpNative.RVA rva = module.ModuleNameRva;

                DumpPointer ptr = TranslateRVA(rva);

                string name = GetString(ptr);
                if ((nameModule == name) ||
                    (name.EndsWith(nameModule)))
                {
                    return new DumpModule(this, module);
                }
            }
            return null;
        }

        /// <summary>
        /// Return the module containing the target address, or null if no match.
        /// </summary>
        /// <param name="targetAddress">address in target</param>
        /// <returns>Null if no match. Else a DumpModule such that the target address is in between the range specified
        /// by the DumpModule's .BaseAddress and .Size property </returns>
        /// <remarks>This can be useful for symbol lookups or for using module images to
        /// supplement memory read requests for minidumps.</remarks>
        public DumpModule TryLookupModuleByAddress(ulong targetAddress)
        {
            // This is an optimized lookup path, which avoids using IEnumerable or creating
            // unnecessary DumpModule objects.
            DumpNative.MINIDUMP_MODULE_LIST list = GetModuleList();

            uint num = list.Count;

            for (uint i = 0; i < num; i++)
            {
                DumpNative.MINIDUMP_MODULE module = list.GetElement(i);
                ulong targetStart = module.BaseOfImage;
                ulong targetEnd = targetStart + module.SizeOfImage;
                if (targetStart <= targetAddress && targetEnd > targetAddress)
                {
                    return new DumpModule(this, module);
                }
            }
            return null;
        }

        /// <summary>
        /// Enumerate all the modules in the dump.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<DumpModule> EnumerateModules()
        {
            DumpNative.MINIDUMP_MODULE_LIST list = GetModuleList();

            uint num = list.Count;

            for (uint i = 0; i < num; i++)
            {
                DumpNative.MINIDUMP_MODULE module = list.GetElement(i);
                yield return new DumpModule(this, module);
            }
        }

        #endregion // Modules

        /*
        public class DumpMemoryRead : IMemoryRead
        {
            public long Address { get; set; }
            public int BytesRequested { get; set; }
            public int BytesRead { get; set; }
            public DumpModule Module { get; set; }
            public FileSearchResult FileSearch { get; set; }
            public override string ToString()
            {
                return ToString("");
            }
            public string ToString(string format)
            {
                StringBuilder sb = new StringBuilder();
                if (BytesRead == 0)
                    sb.Append("Failed ");
                else if (BytesRead < BytesRequested)
                    sb.Append("Partial");
                else
                    sb.Append("Success");
                sb.Append(string.Format(" - 0x{0,-16:x}: 0x{1,-8:x} of 0x{2,-8:x} bytes read", Address, BytesRead, BytesRequested));
                if (format == "detailed")
                {
                    sb.AppendLine();
                    string source = "Dump memory";
                    if (Module != null)
                        source = string.Format("Image {0} (0x{1:x} - 0x{2:x})", Path.GetFileName(Module.FullName),
                            Module.BaseAddress, Module.BaseAddress + Module.Size);
                    sb.AppendLine("Source: " + source);
                    if (FileSearch != null && FileSearch.Path == null)
                    {
                        sb.AppendLine("Image search failed:");
                        sb.AppendLine(FileSearch.ToString());
                    }
                }
                return sb.ToString();
            }
        }
        */
    } // DumpReader


    /// <summary>
    /// Represents a native module in a dump file. This is a flyweight object.
    /// </summary>
    class DumpModule
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="owner">owning DumpReader</param>
        /// <param name="raw">unmanaged dump structure describing the module</param>
        internal DumpModule(DumpReader owner, DumpReader.DumpNative.MINIDUMP_MODULE raw)
        {
            m_raw = raw;
            m_owner = owner;
        }
        DumpReader.DumpNative.MINIDUMP_MODULE m_raw;
        DumpReader m_owner;

        internal DumpReader.DumpNative.MINIDUMP_MODULE Raw
        {
            get
            {
                return m_raw;
            }
        }

        // Since new DumpModule objects are created on each request, override hash code and equals
        // to provide equality so that we can use them in hashes and collections.
        public override bool Equals(object obj)
        {
            DumpModule other = obj as DumpModule;
            if (other == null) return false;
            return (other.m_owner == this.m_owner) && (other.m_raw == this.m_raw);
        }

        // Override of GetHashCode
        public override int GetHashCode()
        {
            // TimeStamp and Checksum are already great 32-bit hash values. 
            // CheckSum may be 0, so use TimeStamp            
            return unchecked((int)m_raw.TimeDateStamp);
        }

        /// <summary>
        ///  Usually, the full filename of the module. Since the dump may not be captured on the local
        ///  machine, be careful of using this filename with the local file system.
        ///  In some cases, this could be a short filename, or unavailable.
        /// </summary>
        public string FullName
        {
            get
            {
                DumpReader.DumpNative.RVA rva = m_raw.ModuleNameRva;
                DumpPointer ptr = m_owner.TranslateRVA(rva);

                string name = m_owner.GetString(ptr);
                return name;
            }
        }

        /// <summary>
        /// Base address within the target of where this module is loaded.
        /// </summary>
        public ulong BaseAddress
        {
            get
            {
                return m_raw.BaseOfImage;
            }
        }

        /// <summary>
        /// Size of this module in bytes as loaded in the target.
        /// </summary>
        public UInt32 Size
        {
            get
            {
                return m_raw.SizeOfImage;
            }
        }

        /// <summary>
        /// UTC Time stamp of module. This is based off a 32-bit value and will overflow in 2038.
        /// This is different than any of the filestamps. Call ToLocalTime() to convert from UTC.
        /// </summary>
        public DateTime Timestamp
        {
            get
            {
                return m_raw.Timestamp;
            }
        }

        /// <summary>
        /// Gets the raw 32 bit time stamp. Use the Timestamp property to get this as a System.DateTime.
        /// </summary>
        public uint RawTimestamp
        {
            get
            {
                return m_raw.TimeDateStamp;
            }
        }

    }

    /// <summary>
    /// Represents a thread from a minidump file. This is a flyweight object.
    /// </summary>
    class DumpThread
    {
        /// <summary>
        /// Constructor for DumpThread
        /// </summary>
        /// <param name="owner">owning DumpReader object</param>
        /// <param name="raw">unmanaged structure in dump describing the thread</param>
        internal DumpThread(DumpReader owner, DumpReader.DumpNative.MINIDUMP_THREAD raw)
        {
            m_raw = raw;
            m_owner = owner;
        }

        DumpReader m_owner;
        DumpReader.DumpNative.MINIDUMP_THREAD m_raw;



        // Since new DumpThread objects are created on each request, override hash code and equals
        // to provide equality so that we can use them in hashes and collections.
        public override bool Equals(object obj)
        {
            DumpThread other = obj as DumpThread;
            if (other == null) return false;
            return (other.m_owner == this.m_owner) && (other.m_raw == this.m_raw);
        }

        public ulong Teb
        {
            get
            {
                return m_raw.Teb;
            }
        }

        // Returns a hash code.
        public override int GetHashCode()
        {
            // Thread Ids are unique random integers within the dump so make a great hash code.
            return ThreadId;
        }

        // Override of ToString
        public override string ToString()
        {
            int id = ThreadId;
            return String.Format(CultureInfo.CurrentUICulture, "Thread {0} (0x{0:x})", id);
        }

        /// <summary>
        /// The native OS Thread Id of this thread.
        /// </summary>
        public int ThreadId
        {
            get
            {
                return (int)m_raw.ThreadId;
            }
        }


        /* todo
        /// <summary>
        /// Safe way to get a thread's context
        /// </summary>
        /// <returns>a native context object representing the thread context</returns>
        public NativeContext GetThreadContext()
        {
            return m_owner.GetThreadContext(m_raw.ThreadContext);
        }

        /// <summary>
        /// Safe way to get a thread's context
        /// </summary>
        public void GetThreadContext(NativeContext context)
        {
            m_owner.GetThreadContext(m_raw.ThreadContext, context);
        }
         */

        /// <summary>
        /// Get a thread's context using a raw buffer and size
        /// </summary>
        public void GetThreadContext(IntPtr buffer, int sizeBufferBytes)
        {
            m_owner.GetThreadContext(m_raw.ThreadContext, buffer, sizeBufferBytes);
        }
    }



    /// <summary>
    /// Utility class to provide various random Native debugging operations.
    /// </summary>
    static class DumpUtility
    {
        // See http://msdn.microsoft.com/msdnmag/issues/02/02/PE/default.aspx for more details

        // The only value of this is to get to at the IMAGE_NT_HEADERS.
        [StructLayout(LayoutKind.Explicit)]
        struct IMAGE_DOS_HEADER
        {      // DOS .EXE header
            [System.Runtime.InteropServices.FieldOffset(0)]
            public short e_magic;                     // Magic number

            /// <summary>
            /// Determine if this is a valid DOS image. 
            /// </summary>
            public bool IsValid
            {
                get
                {
                    return e_magic == 0x5a4d;  // 'MZ'
                }
            }
            // This is the offset of the IMAGE_NT_HEADERS, which is what we really want.
            [System.Runtime.InteropServices.FieldOffset(0x3c)]
            public uint e_lfanew;                    // File address of new exe header
        }

        // Native import for IMAGE_FILE_HEADER.
        [StructLayout(LayoutKind.Sequential)]
        struct IMAGE_FILE_HEADER
        {
            public short Machine;
            public short NumberOfSections;
            public uint TimeDateStamp;
            public uint PointerToSymbolTable;
            public uint NumberOfSymbols;
            public short SizeOfOptionalHeader;
            public short Characteristics;
        }

        // Native import for IMAGE_NT_HEADERs. 
        [StructLayout(LayoutKind.Sequential)]
        struct IMAGE_NT_HEADERS
        {
            public uint Signature;
            public IMAGE_FILE_HEADER FileHeader;


            // Not marshalled.
            //IMAGE_OPTIONAL_HEADER OptionalHeader;

        }

        /// <summary>
        /// Marshal a structure from the given buffer. Effectively returns ((T*) &amp;buffer[offset]).
        /// </summary>
        /// <typeparam name="T">type of structure to marshal</typeparam>
        /// <param name="buffer">array of bytes representing binary buffer to marshal</param>
        /// <param name="offset">offset in buffer to marhsal from</param>
        /// <returns>marshaled structure</returns>
        static T MarshalAt<T>(byte[] buffer, uint offset)
        {
            // Ensure we have enough size in the buffer to copy from.
            int size = Marshal.SizeOf(typeof(T));
            if (offset + size > buffer.Length)
            {
                throw new ArgumentOutOfRangeException();
            }

            GCHandle h = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            IntPtr ptr = h.AddrOfPinnedObject();
            IntPtr p2 = new IntPtr(ptr.ToInt64() + offset);
            T header = default(T);
            Marshal.PtrToStructure(p2, header);

            h.Free();

            return header;
        }

        /// <summary>
        /// Gets the raw compilation timestamp of a file. 
        /// This can be matched with the timestamp of a module in a dump file.
        /// NOTE: This is NOT the same as the file's creation or last-write time.
        /// </summary>
        /// <param name="file"></param>
        /// <returns>0 for common failures like file not found or invalid format. Throws on gross
        /// errors. Else returns the module's timestamp for comparison against the minidump
        /// module's stamp.</returns>
        public static uint GetTimestamp(string file)
        {
            if (!File.Exists(file)) return 0;
            byte[] buffer = File.ReadAllBytes(file);

            IMAGE_DOS_HEADER dos = MarshalAt<IMAGE_DOS_HEADER>(buffer, 0);
            if (!dos.IsValid)
                return 0;

            uint idx = dos.e_lfanew;
            IMAGE_NT_HEADERS header = MarshalAt<IMAGE_NT_HEADERS>(buffer, idx);

            IMAGE_FILE_HEADER f = header.FileHeader;

            return f.TimeDateStamp;
        }
    }

    enum ProcessorArchitecture : ushort
    {
        PROCESSOR_ARCHITECTURE_INTEL = 0,
        PROCESSOR_ARCHITECTURE_MIPS = 1,
        PROCESSOR_ARCHITECTURE_ALPHA = 2,
        PROCESSOR_ARCHITECTURE_PPC = 3,
        PROCESSOR_ARCHITECTURE_SHX = 4,
        PROCESSOR_ARCHITECTURE_ARM = 5,
        PROCESSOR_ARCHITECTURE_IA64 = 6,
        PROCESSOR_ARCHITECTURE_ALPHA64 = 7,
        PROCESSOR_ARCHITECTURE_MSIL = 8,
        PROCESSOR_ARCHITECTURE_AMD64 = 9,
        PROCESSOR_ARCHITECTURE_IA32_ON_WIN64 = 10,
    }
}
