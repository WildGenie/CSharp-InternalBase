﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Gee.External.Capstone;
using Gee.External.Capstone.X86;
using static TestInject.HelperMethods;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace TestInject
{
	public class Memory
	{
		public const string MODULE_NAME = "MyLibrary.dll";
		public static Process HostProcess;
		public static ProcessModule OurModule;

		static Memory(/* string moduleName */)
		{
			HostProcess = Process.GetCurrentProcess();
			/* MODULE_NAME = moduleName */
			OurModule = HostProcess?.FindProcessModule(MODULE_NAME);
		}

		public static void UpdateProcessInformation()
		{
			HostProcess = Process.GetCurrentProcess();
			OurModule = HostProcess?.FindProcessModule(MODULE_NAME);
		}

		public class Reader
		{
			public static unsafe byte[] ReadBytes(IntPtr location, uint numBytes)
			{
				byte[] buff = new byte[numBytes];

				fixed (void* bufferPtr = buff)
				{
					Unsafe.CopyBlockUnaligned(bufferPtr, (void*)location, numBytes);
					return buff;
				}
			}

			public static unsafe T Read<T>(IntPtr location)
				=> Unsafe.Read<T>(location.ToPointer());

			public static string ReadString(IntPtr location, Encoding encodingType, int maxLength = 256)
			{
				var data = ReadBytes(location, (uint)maxLength);
				var text = new string(encodingType.GetChars(data));
				if (text.Contains("\0"))
					text = text.Substring(0, text.IndexOf('\0'));
				return text;
			}

			public static unsafe void CopyBytesToLocation(IntPtr location, IntPtr targetLocation, int numBytes)
			{
				for(int offset = 0; offset < numBytes; offset++)
					*(byte*)(location.ToInt32() + offset) = *(byte*)(targetLocation.ToInt32() + offset);
			}
		}

		public class Writer
		{
			public static unsafe void WriteBytes(IntPtr location, byte[] buffer)
			{
				if (location == IntPtr.Zero) return;
				if (buffer == null || buffer.Length < 1) return;

				var ptr = (void*)location;
				fixed (void* pBuff = buffer)
				{
					Unsafe.CopyBlockUnaligned(ptr, pBuff, (uint)buffer.Length);
				}
			}

			public static unsafe void Write<T>(IntPtr location, T value) 
				=> Unsafe.Write(location.ToPointer(), value);

			public static void WriteString(IntPtr location, string str, Encoding encodingType)
			{
				byte[] bytes = encodingType.GetBytes(str);
				WriteBytes(location, bytes);
			}

			public static unsafe void CopyBytesToLocation(IntPtr location, IntPtr targetLocation, int numBytes)
			{
				for (int offset = 0; offset < numBytes; offset++)
					*(byte*)(location.ToInt32() + offset) = *(byte*)(targetLocation.ToInt32() + offset);
			}
		}

		public class Detours
		{
			public unsafe class HookClass<TDelegate> where TDelegate : Delegate
			{
				public HookClass()
				{

				}

				private static IEnumerable<byte> GetDetourBytes(void* destination, void* origin, bool x86 = true)
				{
					const uint TWO_GB = 2147483648;
					Enums.AssembleType resolvedAssemblyType = x86 ? Enums.AssembleType.X86_32 : Enums.AssembleType.X86_64;

					if (x86)
					{
						uint relativeOffset = destination > origin ? (uint)destination - (uint)origin : (uint)origin - (uint)destination;
						if (Math.Abs(relativeOffset) < Math.Abs(TWO_GB))
							return Assembler.AssembleAsync(new[] { $"jmp 0x{relativeOffset:X}" }, resolvedAssemblyType).GetAwaiter().GetResult();

						return Assembler.AssembleAsync(new[] { $"push 0x{(int)destination:X}", "ret" }, resolvedAssemblyType).GetAwaiter().GetResult();
					}
					else
					{
						long relativeOffset = destination > origin ? (long)destination - (long)origin : (long)origin - (long)destination;
						if (Math.Abs(relativeOffset) < Math.Abs(TWO_GB))
							return Assembler.AssembleAsync(new[] { $"jmp 0x{relativeOffset:X}" }, resolvedAssemblyType).GetAwaiter().GetResult();

						return Assembler.AssembleAsync(new[] { $"mov rax, 0x{(long)destination:X8}", "jmp rax" }, resolvedAssemblyType).GetAwaiter().GetResult();
					}
				}

				public bool Hook(void* function, TDelegate hook, out TDelegate original)
				{
					if (function == null || hook == null)
					{
						if (function == null) Debug.WriteLine($"{nameof(function)} was nullptr");
						if (hook == null) Debug.WriteLine($"{nameof(hook)} was null");

						original = null;
						return false;
					}

					void* hookAddress = Marshal.GetFunctionPointerForDelegate(hook).ToPointer();
					if (hookAddress == null)
					{
						Debug.WriteLine($"{nameof(hookAddress)} was nullptr");
						original = null;
						return false;
					}

					X86DisassembleMode disassmMode =
						IntPtr.Size == 4 ? X86DisassembleMode.Bit32 : X86DisassembleMode.Bit64;

					//using CapstoneX86Disassembler disassm = CapstoneDisassembler.CreateX86Disassembler(disassmMode);
					using (CapstoneX86Disassembler disassm = CapstoneDisassembler.CreateX86Disassembler(disassmMode))
					{
						List<byte> detourShellcode =
												GetDetourBytes(
													hookAddress,
													function,
													disassmMode == X86DisassembleMode.Bit32).ToList();

						int minLength = detourShellcode.Count;
						int offset = 0;

						byte[] functionBytes = new byte[minLength];
						fixed (void* dest = functionBytes)
							Unsafe.CopyBlockUnaligned(dest, function, (uint)minLength);

						X86Instruction[] dissasembly = disassm.Disassemble(functionBytes, (long)function, minLength);

						while (dissasembly == null
							   || dissasembly.Length < 1
							   || dissasembly.Sum(ln => ln.Bytes.Length) < detourShellcode.Count)
						{
							offset++;
							Array.Resize(ref functionBytes, functionBytes.Length + 1);

							fixed (void* dest = functionBytes)
								Unsafe.CopyBlockUnaligned(dest, function, (uint)(minLength + offset));

							dissasembly = disassm.Disassemble(functionBytes, (long)function, minLength + offset);
						}

						if (dissasembly[dissasembly.Length - 1].Bytes.Length == 1
							&& dissasembly[dissasembly.Length - 1].Bytes[0] == 0xC3
							&& dissasembly.Sum(ln => ln.Bytes.Length) < detourShellcode.Count)
						{

							Debug.WriteLine($"Function reached end before getting enough bytes ({detourShellcode.Count}) to work with");
							original = null;
							return false;
						}

						int nopCount = dissasembly.Sum(ln => ln.Bytes.Length) - detourShellcode.Count;
						if (nopCount > 0) detourShellcode.AddRange(Enumerable.Range(0, nopCount).Select(b => (byte)0x90));

						List<byte> originalFunctionBytes = dissasembly.SelectMany(ln => ln.Bytes).ToList();
						originalFunctionBytes.AddRange(
							disassmMode == X86DisassembleMode.Bit32
								? Assembler.AssembleAsync(new[] { $"push 0x{(int)function + detourShellcode.Count + nopCount:X}", "ret" }, Enums.AssembleType.X86_32).GetAwaiter().GetResult()
								: Assembler.AssembleAsync(new[] { "jmp qword ptr [rip]" }, Enums.AssembleType.X86_64).GetAwaiter().GetResult());

						if (disassmMode == X86DisassembleMode.Bit64) // writing the qword as data for the 64bit [rip] jmp
							originalFunctionBytes.AddRange(BitConverter.GetBytes((long)function + detourShellcode.Count + nopCount));


						void* originalFunctionAllocation = Marshal.AllocHGlobal(originalFunctionBytes.Count).ToPointer();
						if (originalFunctionAllocation == null)
						{
							Debug.WriteLine("Failed to allocate for original function body");
							original = null;
							return false;
						}

						// Change protection of our allocated region to EXECUTE_READ_WRITE
						if (!PInvoke.VirtualProtect(
							(IntPtr)originalFunctionAllocation,
							originalFunctionBytes.Count,
							Enums.MemoryProtection.ExecuteReadWrite,
							out _))
						{
							Marshal.FreeHGlobal((IntPtr)originalFunctionAllocation);

							original = null;
							return false;
						}

						fixed (void* src = originalFunctionBytes.ToArray())
							Unsafe.CopyBlockUnaligned(originalFunctionAllocation, src, (uint)originalFunctionBytes.Count);

						if (!PInvoke.VirtualProtect(
							new IntPtr(function),
							functionBytes.Length,
							Enums.MemoryProtection.ExecuteReadWrite /* RWX */,
							out Enums.MemoryProtection oldProtect))
						{
							Marshal.FreeHGlobal((IntPtr)originalFunctionAllocation);

							original = null;
							return false;
						}

						fixed (void* src = detourShellcode.ToArray())
							Unsafe.CopyBlockUnaligned(function, src, (uint)detourShellcode.Count);

						if (!PInvoke.VirtualProtect(
							new IntPtr(function),
							functionBytes.Length,
							oldProtect,
							out _))
						{
							Debug.WriteLine($"Failed restoring protection for 0x{(long)function:X8}");
						}

						original = Marshal.GetDelegateForFunctionPointer<TDelegate>((IntPtr)originalFunctionAllocation);
						return original != null;
					}
				}
			}
		}

		public class Pattern
		{
			public static unsafe ulong FindPatternExecutable(string processModule, string pattern, bool resultAbsolute = true)
			{
				UpdateProcessInformation();

				var tmpSplitPattern = pattern.TrimStart(' ').TrimEnd(' ').Split(' ');
				var tmpPattern = new byte[tmpSplitPattern.Length];
				var tmpMask = new byte[tmpSplitPattern.Length];

				for (var i = 0; i < tmpSplitPattern.Length; i++)
				{
					var ba = tmpSplitPattern[i];

					if (ba == "??" || ba.Length == 1 && ba == "?")
					{
						tmpMask[i] = 0x00;
						tmpSplitPattern[i] = "0x00";
					}
					else if (char.IsLetterOrDigit(ba[0]) && ba[1] == '?')
					{
						tmpMask[i] = 0xF0;
						tmpSplitPattern[i] = ba[0] + "0";
					}
					else if (char.IsLetterOrDigit(ba[1]) && ba[0] == '?')
					{
						tmpMask[i] = 0x0F;
						tmpSplitPattern[i] = "0" + ba[1];
					}
					else
					{
						tmpMask[i] = 0xFF;
					}
				}

				for (var i = 0; i < tmpSplitPattern.Length; i++)
					tmpPattern[i] = (byte)(Convert.ToByte(tmpSplitPattern[i], 16) & tmpMask[i]);

				if (tmpMask.Length != tmpPattern.Length)
					throw new ArgumentException($"{nameof(pattern)}.Length != {nameof(tmpMask)}.Length");

				if (string.IsNullOrEmpty(processModule))
				{
					// do shut
					return 0;
				}

				ProcessModule pm = HostProcess.Modules.Cast<ProcessModule>().FirstOrDefault(x => string.Equals(x.ModuleName, processModule, StringComparison.CurrentCultureIgnoreCase));
				if (pm == null)
					return 0;

				byte[] buffer = new byte[pm.ModuleMemorySize];
				try
				{
					buffer = Reader.ReadBytes(pm.BaseAddress, (uint)pm.ModuleMemorySize);
				}
				catch
				{
					Console.WriteLine($"ReadBytes(location: 0x{pm.BaseAddress.ToInt32():X8}, numBytes: {buffer.Length}) failed ...");
					return 0;
				}

				if (buffer == null || buffer.Length < 1) return 0;

				long result = 0 - tmpPattern.LongLength;
				fixed (byte* pPacketBuffer = buffer)
				{
					do
					{
						result = HelperMethods.FindPattern(pPacketBuffer, buffer.Length, tmpPattern, tmpMask, result + tmpPattern.LongLength);
						if (result >= 0)
							return resultAbsolute ? (ulong)pm.BaseAddress.ToInt64() + (ulong)result : (ulong)result;
					} while (result != -1);
				}
				return 0;
			}

			public static unsafe List<long> FindPattern(string pattern, string optionalModuleName, bool readable, bool writable, bool executable)
			{
				#region Creation of Byte Array from string pattern
				var tmpSplitPattern = pattern.TrimStart(' ').TrimEnd(' ').Split(' ');
				var tmpPattern = new byte[tmpSplitPattern.Length];
				var tmpMask = new byte[tmpSplitPattern.Length];

				for (var i = 0; i < tmpSplitPattern.Length; i++)
				{
					var ba = tmpSplitPattern[i];

					if (ba == "??" || ba.Length == 1 && ba == "?")
					{
						tmpMask[i] = 0x00;
						tmpSplitPattern[i] = "0x00";
					}
					else if (char.IsLetterOrDigit(ba[0]) && ba[1] == '?')
					{
						tmpMask[i] = 0xF0;
						tmpSplitPattern[i] = ba[0] + "0";
					}
					else if (char.IsLetterOrDigit(ba[1]) && ba[0] == '?')
					{
						tmpMask[i] = 0x0F;
						tmpSplitPattern[i] = "0" + ba[1];
					}
					else
					{
						tmpMask[i] = 0xFF;
					}
				}


				for (var i = 0; i < tmpSplitPattern.Length; i++)
					tmpPattern[i] = (byte)(Convert.ToByte(tmpSplitPattern[i], 16) & tmpMask[i]);

				if (tmpMask.Length != tmpPattern.Length)
					throw new ArgumentException($"{nameof(pattern)}.Length != {nameof(tmpMask)}.Length");
				#endregion

				Structures.SYSTEM_INFO si = new Structures.SYSTEM_INFO();
				PInvoke.GetSystemInfo(ref si);

				ConcurrentBag<(IntPtr RegionBase, IntPtr RegionSize)> regions = new ConcurrentBag<(IntPtr RegionBase, IntPtr RegionSize)>();
				ConcurrentQueue<long> results = new ConcurrentQueue<long>();

				ProcessModule pm = null;
				if (!string.IsNullOrEmpty(optionalModuleName))
				{
					UpdateProcessInformation();
					pm = HostProcess.FindProcessModule(optionalModuleName);
					if (pm == null)
						throw new Exception($"Cannot find module '{optionalModuleName}'");
				}

				uint lpMem = (pm != null && optionalModuleName != "") 
					? (uint)pm.BaseAddress 
					: (uint) si.lpMinimumApplicationAddress;

				if (lpMem < (uint) si.lpMinimumApplicationAddress)
					lpMem = (uint) si.lpMinimumApplicationAddress;

				uint maxAddress = (pm != null && optionalModuleName != ""
					? (uint) (pm.BaseAddress + pm.ModuleMemorySize)
					: (uint) si.lpMaximumApplicationAddress);

				if (maxAddress > (uint) si.lpMaximumApplicationAddress)
					maxAddress = (uint) si.lpMaximumApplicationAddress;

				//uint lpMem = (uint)si.lpMinimumApplicationAddress;

					// while (lpMem < ((uint)si.lpMaximumApplicationAddress))
				while (lpMem < maxAddress)
				{
					if (PInvoke.VirtualQuery((IntPtr)lpMem,
							out Structures.MEMORY_BASIC_INFORMATION lpBuffer,
							(uint)Marshal.SizeOf<Structures.MEMORY_BASIC_INFORMATION>()) != 0)
					{
						var buff = &lpBuffer;

						bool isValid = buff->State == Enums.MemoryState.MEM_COMMIT;
						isValid &= (uint)(buff->BaseAddress) < (uint)si.lpMaximumApplicationAddress;
						isValid &= ((buff->Protect & Enums.MemoryProtection.GuardModifierflag) == 0);
						isValid &= ((buff->Protect & Enums.MemoryProtection.NoAccess) == 0);
						isValid &= (buff->Type == Enums.MemoryType.MEM_PRIVATE) || (buff->Type == Enums.MemoryType.MEM_IMAGE);

						if (isValid)
						{
							bool isReadable = (buff->Protect & Enums.MemoryProtection.ReadOnly) > 0;



							bool isWritable = ((buff->Protect & Enums.MemoryProtection.ReadWrite) > 0) ||
											  ((buff->Protect & Enums.MemoryProtection.WriteCopy) > 0) ||
											  ((buff->Protect & Enums.MemoryProtection.ExecuteReadWrite) > 0) ||
											  ((buff->Protect & Enums.MemoryProtection.ExecuteWriteCopy) > 0);

							bool isExecutable = ((buff->Protect & Enums.MemoryProtection.Execute) > 0) ||
												((buff->Protect & Enums.MemoryProtection.ExecuteRead) > 0) ||
												((buff->Protect & Enums.MemoryProtection.ExecuteReadWrite) > 0) ||
												((buff->Protect & Enums.MemoryProtection.ExecuteWriteCopy) > 0);

							isReadable &= readable;
							isWritable &= writable;
							isExecutable &= executable;

							isValid &= isReadable || isWritable || isExecutable;

							if (isValid)
								regions.Add((buff->BaseAddress, buff->RegionSize));

						}

						lpMem = (uint)buff->BaseAddress + (uint)buff->RegionSize;
					}
				}

				Parallel.ForEach(regions, (currentRegion) =>
				{
					long result = 0 - tmpPattern.LongLength;
					do
					{
						var (regionBase, regionSize) = currentRegion;
						result = HelperMethods.FindPattern((byte*)regionBase, (int)regionSize, tmpPattern, tmpMask, result + tmpPattern.LongLength);
						if (result >= 0)
							results.Enqueue((long)regionBase + result);

					} while (result != -1);
				});

				return results.ToList().OrderBy(address => address).ToList();
			}
		}

		public class Assembler
		{
			public static async Task<byte[]> AssembleAsync(
				string[] mnemonics,
				Enums.AssembleType assembleType = Enums.AssembleType.X86_32)
			{
				if (mnemonics.Length < 1)
					return null;

				string encodedMnemonics = HttpUtility.UrlEncode(string.Join("\n", mnemonics));
				string type = TranslateAssembleType(assembleType);
				if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
					return null;

				string requestUrl = $"http://shell-storm.org/online/Online-Assembler-and-Disassembler/?inst={encodedMnemonics}&arch={type}&as_format=inline";

				using (HttpClient req = new HttpClient())
				{
					string responseBody = await req.GetStringAsync(requestUrl).ConfigureAwait(false);

					if (string.IsNullOrEmpty(responseBody))
						return null;

					HtmlDocument doc = new HtmlDocument();
					doc.LoadHtml(responseBody);
					var nodes = doc.DocumentNode.SelectNodes(".//pre");
					if (nodes == null || nodes.Count < 1)
						return null;

					string bytesString = nodes[0].InnerText;
					if (bytesString.Equals("Invalid instruction(s)", StringComparison.InvariantCultureIgnoreCase))
						return null;

					bytesString = bytesString.Trim('"');
					string[] bytesStringArray = bytesString.Split(new[] { "\\x" }, StringSplitOptions.RemoveEmptyEntries);

					return bytesStringArray.Select(strByte => byte.Parse(strByte, NumberStyles.HexNumber)).ToArray();
				}
				
			}

			public static X86Instruction[] Dissasemble(byte[] buffer, X86DisassembleMode disassmMode = X86DisassembleMode.Bit32, long startingAddress = 0L)
			{
				if (buffer == null || buffer.Length < 1)
					return null;

				using (CapstoneX86Disassembler disassm = CapstoneDisassembler.CreateX86Disassembler(disassmMode))
				{
					return disassm.Disassemble(buffer, startingAddress);
				}
			}
		}

		public class Allocator
		{
			public class Managed
			{
				public static IntPtr ManagedAllocate(int size, Enums.MemoryProtection flMemProtectType = Enums.MemoryProtection.ExecuteReadWrite)
				{
					IntPtr alloc = Marshal.AllocHGlobal(size);
					if (alloc != IntPtr.Zero)
						Protection.SetPageProtection(alloc, size, flMemProtectType, out var old);

					return alloc;
				}

				public static void ManagedFree(IntPtr address)
					=> Marshal.FreeHGlobal(address);
			}
			public class Unmanaged
			{
				public static IntPtr Allocate(uint size, Enums.AllocationType flAllocType = Enums.AllocationType.Commit | Enums.AllocationType.Reserve, Enums.MemoryProtection flMemProtectType = Enums.MemoryProtection.ExecuteReadWrite)
					=> PInvoke.VirtualAlloc(IntPtr.Zero, new UIntPtr(size), flAllocType, flMemProtectType);

				public static bool FreeMemory(IntPtr address, uint optionalSize = 0)
					=> PInvoke.VirtualFree(address, optionalSize, Enums.FreeType.Release);

				public class Extended
				{
					private static IntPtr _heapObject;

					public static IntPtr AllocateHeap(uint size, uint dwFlags = 0x00000004 | 0x00000008)
					{
						if (_heapObject == IntPtr.Zero)
						{
							_heapObject = PInvoke.HeapCreate(0x00040000 | 0x00000004, new UIntPtr(0),
								new UIntPtr(0));

							if (_heapObject == IntPtr.Zero) throw new Exception($"HeapCreate failed!");
						}
							

						return size < 1 ? IntPtr.Zero : PInvoke.HeapAlloc(_heapObject, dwFlags, (UIntPtr) size);
					}

					public static bool FreeHeap(IntPtr addr, uint dwFlags = 0x00000004 | 0x00000008)
					{
						return _heapObject != IntPtr.Zero && PInvoke.HeapFree(_heapObject, dwFlags, addr);
					}
				}
			}

			public IntPtr FindEmptySpaceInRegion(IntPtr targetRegion, int desiredSize)
			{
				IntPtr minAddress = IntPtr.Subtract(targetRegion, 0x70000000);
				IntPtr maxAddress = IntPtr.Add(targetRegion, 0x70000000);

				IntPtr ret = IntPtr.Zero;
				IntPtr tmpAddress = IntPtr.Zero;

				Structures.SYSTEM_INFO si = new Structures.SYSTEM_INFO();
				PInvoke.GetSystemInfo(ref si);

				if (Environment.Is64BitProcess)
				{
					if ((long)minAddress > (long)si.lpMaximumApplicationAddress ||
					    (long)minAddress < (long)si.lpMinimumApplicationAddress)
						minAddress = si.lpMinimumApplicationAddress;

					if ((long)maxAddress < (long)si.lpMinimumApplicationAddress ||
					    (long)maxAddress > (long)si.lpMaximumApplicationAddress)
						maxAddress = si.lpMaximumApplicationAddress;
				}
				else
				{
					minAddress = si.lpMinimumApplicationAddress;
					maxAddress = si.lpMaximumApplicationAddress;
				}

				IntPtr current = minAddress;
				IntPtr previous = current;

				while (PInvoke.VirtualQuery(current, out var mbi, (uint) Marshal.SizeOf<Structures.MEMORY_BASIC_INFORMATION>()) != 0)
				{
					if ((long) mbi.BaseAddress > (long) maxAddress)
						return IntPtr.Zero; // No memory found, let windows handle

					if (mbi.State == Enums.MemoryState.MEM_FREE && (int) mbi.RegionSize > desiredSize)
					{
						if ((long) mbi.BaseAddress % si.dwAllocationGranularity > 0)
						{
							// The whole size can not be used
							tmpAddress = mbi.BaseAddress;
							int offset = (int) (si.dwAllocationGranularity -
							                    ((long) tmpAddress % si.dwAllocationGranularity));
							// Check if there is enough left
							if ((int) (mbi.RegionSize - offset) >= desiredSize)
							{
								if ((long) tmpAddress < (long)targetRegion)
								{
									tmpAddress = IntPtr.Add(tmpAddress, (int)(mbi.RegionSize - offset - desiredSize));

									if ((long)tmpAddress > (long)targetRegion)
										tmpAddress = targetRegion;

									// decrease tmpAddress until its alligned properly
									tmpAddress = IntPtr.Subtract(tmpAddress, (int)((long)tmpAddress % si.dwAllocationGranularity));
								}

								if (Math.Abs((long)tmpAddress - (long)targetRegion) < Math.Abs((long)ret - (long)targetRegion))
									ret = tmpAddress;
							}
						}
						else
						{
							tmpAddress = mbi.BaseAddress;

							if ((long)tmpAddress < (long)targetRegion) // try to get it the cloest possible 
								// (so to the end of the region - size and
								// aligned by system allocation granularity)
							{
								tmpAddress = IntPtr.Add(tmpAddress, (int)(mbi.RegionSize - desiredSize));

								if ((long)tmpAddress > (long)targetRegion)
									tmpAddress = targetRegion;

								// decrease until aligned properly
								tmpAddress =
									IntPtr.Subtract(tmpAddress, (int)((long)tmpAddress % si.dwAllocationGranularity));
							}

							if (Math.Abs((long)tmpAddress - (long)targetRegion) < Math.Abs((long)ret - (long)targetRegion))
								ret = tmpAddress;
						}
					}

					if ((int)mbi.RegionSize % si.dwAllocationGranularity > 0)
						mbi.RegionSize += (int)(si.dwAllocationGranularity - ((int)mbi.RegionSize % (int)si.dwAllocationGranularity));

					previous = current;
					current = IntPtr.Add(mbi.BaseAddress, (int)mbi.RegionSize);

					if ((long)current > (long)maxAddress)
						return ret;

					if ((long)previous > (long)current)
						return ret; // Overflow

				}

				return IntPtr.Zero;
			}
		}

		public class Protection
		{
			public static bool SetPageProtection(IntPtr baseAddress, int size, Enums.MemoryProtection newProtection, out Enums.MemoryProtection oldProtection)
			{
				bool res = PInvoke.VirtualProtect(baseAddress, size, newProtection, out var oldProtect);
				oldProtection = oldProtect;
				return res;
			}
			public static bool GetPageProtection(IntPtr baseAddress, out Structures.MEMORY_BASIC_INFORMATION pageinfo)
			{
				int res = PInvoke.VirtualQuery(baseAddress,
					out pageinfo, 
					(uint)Marshal.SizeOf<Structures.MEMORY_BASIC_INFORMATION>());
				return res == Marshal.SizeOf<Structures.MEMORY_BASIC_INFORMATION>();
			}
		}

		public class Threads
		{
			public static void SuspendProcess()
			{
				UpdateProcessInformation();

				ProcessModule ourModule = OurModule;
				ProcessModule clrJit = HostProcess.FindProcessModule("clrjit.dll");
				ProcessModule clr = HostProcess.FindProcessModule("clr.dll");

				foreach (ProcessThread pT in HostProcess.Threads)
				{
					if (AddressResidesWithinModule(pT.StartAddress, ourModule, "our module") ||
					    AddressResidesWithinModule(pT.StartAddress, clrJit, "clrJit") ||
					    AddressResidesWithinModule(pT.StartAddress, clr, "clr"))
						continue;

					IntPtr pOpenThread = PInvoke.OpenThread(Enums.ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);
					if (pOpenThread == IntPtr.Zero)
						continue;

					PInvoke.SuspendThread(pOpenThread);
					PInvoke.CloseHandle(pOpenThread);
				}
			}
			public static void ResumeProcess()
			{
				if (HostProcess.ProcessName == string.Empty)
					return;

				foreach (ProcessThread pT in HostProcess.Threads)
				{
					IntPtr pOpenThread = PInvoke.OpenThread(Enums.ThreadAccess.SUSPEND_RESUME, false, (uint)pT.Id);

					if (pOpenThread == IntPtr.Zero)
						continue;

					var suspendCount = 0;
					do
					{
						suspendCount = PInvoke.ResumeThread(pOpenThread);
					} while (suspendCount > 0);

					PInvoke.CloseHandle(pOpenThread);
				}
			}

			private static bool AddressResidesWithinModule(IntPtr address, ProcessModule processModule, string moduleDescriptor)
			{
				if (processModule == null)
					throw new Exception($"AddressResidesWithinModule - Module with descriptor '{moduleDescriptor}' was null");

				long modEnd = processModule.BaseAddress.ToInt64() + processModule.ModuleMemorySize;
				return address.ToInt64() >= processModule.BaseAddress.ToInt64() && address.ToInt64() <= modEnd;
			}
		}

		public class Modules
		{
			public static IntPtr GetModuleBaseAddress(string moduleName)
			{
				UpdateProcessInformation();
				foreach (ProcessModule pm in HostProcess.Modules)
					if (string.Equals(pm.ModuleName, moduleName, StringComparison.CurrentCultureIgnoreCase))
						return pm.BaseAddress;
				return PInvoke.GetModuleHandle(moduleName);
			}
		}

		public class Structures
		{
			[StructLayout(LayoutKind.Sequential)]
			internal struct SYSTEM_INFO
			{
				internal ushort wProcessorArchitecture;
				internal ushort wReserved;
				internal uint dwPageSize;
				internal IntPtr lpMinimumApplicationAddress;
				internal IntPtr lpMaximumApplicationAddress;
				internal IntPtr dwActiveProcessorMask;
				internal uint dwNumberOfProcessors;
				internal uint dwProcessorType;
				internal uint dwAllocationGranularity;
				internal ushort wProcessorLevel;
				internal ushort wProcessorRevision;
			}

			[StructLayout(LayoutKind.Sequential)]
			public struct Vector4
			{
				public float X;
				public float Y;
				public float Z;
				public float W;
			}

			[StructLayout(LayoutKind.Sequential, Pack = 1)]
			public struct Vector
			{
				public float X;
				public float Y;

				public Vector(float x, float y)
				{
					X = x;
					Y = y;
				}

				public float Distance(Vector vector)
				{
					float dx = vector.X - X;
					float dy = vector.Y - Y;
					return (float)Math.Sqrt(dx * dx + dy * dy);
				}
			}

			[StructLayout(LayoutKind.Sequential)]
			public struct D3DMATRIX
			{
				public float _11, _12, _13, _14;
				public float _21, _22, _23, _24;
				public float _31, _32, _33, _34;
				public float _41, _42, _43, _44;

				public float[][] As2DArray()
				{
					return new float[4][]
					{
						new[] { _11, _12, _13, _14 },
						new[] { _21, _22, _23, _24 },
						new[] { _31, _32, _33, _34 },
						new[] { _41, _42, _43, _44 },
					};
				}
				public float[] AsArray()
				{
					return new[]
					{
						_11, _12, _13, _14,
						_21, _22, _23, _24,
						_31, _32, _33, _34,
						_41, _42, _43, _44
					};
				}
			}

			[StructLayout(LayoutKind.Sequential, Pack = 1)]
			public unsafe struct Vector3
			{
				public Vector3(float x, float y, float z)
				{
					X = x;
					Y = y;
					Z = z;
				}

				public float X;
				public float Y;
				public float Z;

				public Vector3 Zero => new Vector3(0, 0, 0);

				public bool World2Screen(float[] matrix, out Vector screenPosition)
				{
					if (matrix == null || matrix.Length != 16)
					{
						screenPosition = new Vector(0f, 0f);
						return false;
					}

					Vector4 vec = new Vector4
					{
						X = this.X * matrix[0] + this.Y * matrix[4] + this.Z * matrix[8] + matrix[12],
						Y = this.X * matrix[1] + this.Y * matrix[5] + this.Z * matrix[9] + matrix[13],
						Z = this.X * matrix[2] + this.Y * matrix[6] + this.Z * matrix[10] + matrix[14],
						W = this.X * matrix[3] + this.Y * matrix[7] + this.Z * matrix[11] + matrix[15]
					};

					if (vec.W < 0.1f)
					{
						screenPosition = new Vector(0f, 0f);
						return false;
					}

					Vector3 NDC = new Vector3
					{
						X = vec.X / vec.W,
						Y = vec.Y / vec.W,
						Z = vec.Z / vec.Z
					};

					int* w = (int*)0x00510C94;
					int* h = (int*)0x00510C98;


					screenPosition = new Vector
					{
						X = (*w / 2 * NDC.X) + (NDC.X + *w / 2),
						Y = -(*h / 2 * NDC.Y) + (NDC.Y + *h / 2)
					};
					return true;
				}
				public bool World2Screen(float* matrix, out Vector screenPosition)
				{
					if (matrix == null)
					{
						screenPosition = new Vector(0f, 0f);
						return false;
					}

					Vector4 vec = new Vector4
					{
						X = this.X * matrix[0] + this.Y * matrix[4] + this.Z * matrix[8] + matrix[12],
						Y = this.X * matrix[1] + this.Y * matrix[5] + this.Z * matrix[9] + matrix[13],
						Z = this.X * matrix[2] + this.Y * matrix[6] + this.Z * matrix[10] + matrix[14],
						W = this.X * matrix[3] + this.Y * matrix[7] + this.Z * matrix[11] + matrix[15]
					};

					if (vec.W < 0.1f)
					{
						screenPosition = new Vector(0f, 0f);
						return false;
					}

					Vector3 NDC = new Vector3
					{
						X = vec.X / vec.W,
						Y = vec.Y / vec.W,
						Z = vec.Z / vec.Z
					};

					int* w = (int*)0x00510C94;
					int* h = (int*)0x00510C98;


					screenPosition = new Vector
					{
						X = (*w / 2 * NDC.X) + (NDC.X + *w / 2),
						Y = -(*h / 2 * NDC.Y) + (NDC.Y + *h / 2)
					};
					return true;
				}

				public float Max => (X > Y) ? ((X > Z) ? X : Z) : ((Y > Z) ? Y : Z);
				public float Min => (X < Y) ? ((X < Z) ? X : Z) : ((Y < Z) ? Y : Z);
				public float EuclideanNorm => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
				public float Square => X * X + Y * Y + Z * Z;
				public float Magnitude => (float)Math.Sqrt(SumComponentSqrs());
				public float Distance3D(Vector3 v1, Vector3 v2)
				{
					return
						(float)Math.Sqrt
						(
							(v1.X - v2.X) * (v1.X - v2.X) +
							(v1.Y - v2.Y) * (v1.Y - v2.Y) +
							(v1.Z - v2.Z) * (v1.Z - v2.Z)
						);
				}
				public float Distance3D(Vector3 other)
				{
					return Distance3D(this, other);
				}

				public float Normalize()
				{
					float norm = (float)System.Math.Sqrt(X * X + Y * Y + Z * Z);
					float invNorm = 1.0f / norm;

					X *= invNorm;
					Y *= invNorm;
					Z *= invNorm;

					return norm;
				}
				public Vector3 Inverse()
				{
					return new Vector3(
						(X == 0) ? 0 : 1.0f / X,
						(Y == 0) ? 0 : 1.0f / Y,
						(Z == 0) ? 0 : 1.0f / Z);
				}
				public Vector3 Abs()
				{
					return new Vector3(Math.Abs(X), Math.Abs(Y), Math.Abs(Z));
				}
				public Vector3 CrossProduct(Vector3 vector1, Vector3 vector2)
				{
					return new Vector3(
						vector1.Y * vector2.Z - vector1.Z * vector2.Y,
						vector1.Z * vector2.X - vector1.X * vector2.Z,
						vector1.X * vector2.Y - vector1.Y * vector2.X);
				}
				public float DotProduct(Vector3 vector1, Vector3 vector2)
				{
					return vector1.X * vector2.X + vector1.Y * vector2.Y + vector1.Z * vector2.Z;
				}


				public override string ToString()
				{
					return string.Format(CultureInfo.InvariantCulture,
						"{0}, {1}, {2}", X, Y, Z);
				}
				public float[] ToArray()
				{
					return new float[3] { X, Y, Z };
				}

				public float this[int index]
				{
					get
					{
						switch (index)
						{
							case 0: { return X; }
							case 1: { return Y; }
							case 2: { return Z; }
							default: throw new IndexOutOfRangeException($"Range is from 0 to 2");
						}
					}
				}

				public static Vector3 operator +(Vector3 vector, float value)
				{
					return new Vector3(vector.X + value, vector.Y + value, vector.Z + value);
				}
				public static Vector3 operator +(Vector3 vector1, Vector3 vector2)
				{
					return new Vector3(vector1.X + vector2.X, vector1.Y + vector2.Y, vector1.Z + vector2.Z);
				}
				public Vector3 Add(Vector3 vector1, Vector3 vector2)
				{
					return vector1 + vector2;
				}
				public Vector3 Add(Vector3 vector, float value)
				{
					return vector + value;
				}

				private Vector3 SqrComponents(Vector3 v1)
				{
					return
					(
						new Vector3
						(
							v1.X * v1.X,
							v1.Y * v1.Y,
							v1.Z * v1.Z
						)
					);
				}
				private double SumComponentSqrs(Vector3 v1)
				{
					Vector3 v2 = SqrComponents(v1);
					return v2.SumComponents();
				}
				private double SumComponentSqrs()
				{
					return SumComponentSqrs(this);
				}
				private double SumComponents(Vector3 v1)
				{
					return (v1.X + v1.Y + v1.Z);
				}
				private double SumComponents()
				{
					return SumComponents(this);
				}

				public static Vector3 operator -(Vector3 vector1, Vector3 vector2)
				{
					return new Vector3(vector1.X - vector2.X, vector1.Y - vector2.Y, vector1.Z - vector2.Z);
				}
				public Vector3 Subtract(Vector3 vector1, Vector3 vector2)
				{
					return vector1 - vector2;
				}
				public static Vector3 operator -(Vector3 vector, float value)
				{
					return new Vector3(vector.X - value, vector.Y - value, vector.Z - value);
				}
				public Vector3 Subtract(Vector3 vector, float value)
				{
					return vector - value;
				}

				public static Vector3 operator *(Vector3 vector1, Vector3 vector2)
				{
					return new Vector3(vector1.X * vector2.X, vector1.Y * vector2.Y, vector1.Z * vector2.Z);
				}
				public Vector3 Multiply(Vector3 vector1, Vector3 vector2)
				{
					return vector1 * vector2;
				}
				public static Vector3 operator *(Vector3 vector, float factor)
				{
					return new Vector3(vector.X * factor, vector.Y * factor, vector.Z * factor);
				}
				public Vector3 Multiply(Vector3 vector, float factor)
				{
					return vector * factor;
				}

				public static Vector3 operator /(Vector3 vector1, Vector3 vector2)
				{
					return new Vector3(vector1.X / vector2.X, vector1.Y / vector2.Y, vector1.Z / vector2.Z);
				}
				public Vector3 Divide(Vector3 vector1, Vector3 vector2)
				{
					return vector1 / vector2;
				}
				public static Vector3 operator /(Vector3 vector, float factor)
				{
					return new Vector3(vector.X / factor, vector.Y / factor, vector.Z / factor);
				}
				public Vector3 Divide(Vector3 vector, float factor)
				{
					return vector / factor;
				}

				public static bool operator ==(Vector3 vector1, Vector3 vector2)
				{
					return ((vector1.X == vector2.X) && (vector1.Y == vector2.Y) && (vector1.Z == vector2.Z));
				}
				public static bool operator !=(Vector3 vector1, Vector3 vector2)
				{
					return ((vector1.X != vector2.X) || (vector1.Y != vector2.Y) || (vector1.Z != vector2.Z));
				}

				public static bool operator <(Vector3 v1, Vector3 v2)
				{
					return v1.SumComponentSqrs() < v2.SumComponentSqrs();
				}
				public static bool operator <=(Vector3 v1, Vector3 v2)
				{
					return v1.SumComponentSqrs() <= v2.SumComponentSqrs();
				}

				public static bool operator >=(Vector3 v1, Vector3 v2)
				{
					return v1.SumComponentSqrs() >= v2.SumComponentSqrs();
				}
				public static bool operator >(Vector3 v1, Vector3 v2)
				{
					return v1.SumComponentSqrs() > v2.SumComponentSqrs();
				}


				public bool Equals(Vector3 vector)
				{
					return ((vector.X == X) && (vector.Y == Y) && (vector.Z == Z));
				}
				public override bool Equals(object obj)
				{
					if (obj is Vector3 vector3)
					{
						return Equals(vector3);
					}
					return false;
				}

				public override int GetHashCode()
				{
					return X.GetHashCode() + Y.GetHashCode() + Z.GetHashCode();
				}
			}

			[StructLayout(LayoutKind.Sequential)]
			public struct MEMORY_BASIC_INFORMATION
			{
				public IntPtr BaseAddress;
				public IntPtr AllocationBase;
				public Enums.AllocationType AllocationProtect;
				public IntPtr RegionSize;
				public Enums.MemoryState State;
				public Enums.MemoryProtection Protect;
				public Enums.MemoryType Type;
			}
		}
		public class Enums	
		{
			public enum AssembleType
			{
				ARM,
				ArmThumb,
				AArch64,
				Mips32,
				Mips64,
				PowerPC32,
				PowerPC64,
				Sparc,
				X86_16,
				X86_32,
				X86_64,
			}

			[Flags]
			public enum AllocationType
			{
				Commit = 0x1000,
				Reserve = 0x2000,
				Decommit = 0x4000,
				Release = 0x8000,
				Reset = 0x80000,
				Physical = 0x400000,
				TopDown = 0x100000,
				WriteWatch = 0x200000,
				LargePages = 0x20000000
			}

			[Flags]
			public enum MemoryProtection
			{
				Execute = 0x10,
				ExecuteRead = 0x20,
				ExecuteReadWrite = 0x40,
				ExecuteWriteCopy = 0x80,
				NoAccess = 0x01,
				ReadOnly = 0x02,
				ReadWrite = 0x04,
				WriteCopy = 0x08,
				GuardModifierflag = 0x100,
				NoCacheModifierflag = 0x200,
				WriteCombineModifierflag = 0x400
			}

			public enum FreeType
			{
				Decommit = 0x4000,
				Release = 0x8000,
			}

			[Flags]
			public enum MemoryState : uint
			{
				MEM_COMMIT = 0x1000,
				MEM_FREE = 0x10000,
				MEM_RESERVE = 0x2000
			}

			[Flags]
			public enum MemoryType : uint
			{
				MEM_IMAGE = 0x1000000,
				MEM_MAPPED = 0x40000,
				MEM_PRIVATE = 0x20000
			}

			[Flags]
			public enum DesiredAccess : uint
			{
				GenericRead = 0x80000000,
				GenericWrite = 0x40000000,
				GenericExecute = 0x20000000,
				GenericAll = 0x10000000
			}

			public enum StdHandle : int
			{
				Input = -10,
				Output = -11,
				Error = -12
			}

			public enum ThreadAccess : int
			{
				TERMINATE = (0x0001),
				SUSPEND_RESUME = (0x0002),
				GET_CONTEXT = (0x0008),
				SET_CONTEXT = (0x0010),
				SET_INFORMATION = (0x0020),
				QUERY_INFORMATION = (0x0040),
				SET_THREAD_TOKEN = (0x0080),
				IMPERSONATE = (0x0100),
				DIRECT_IMPERSONATION = (0x0200)
			}
		}
		public class PInvoke
		{
			[DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
			public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

			[DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
			public static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPStr)]string lpFileName);

			[DllImport("kernel32.dll", SetLastError = true)]

			internal static extern void GetSystemInfo(ref Structures.SYSTEM_INFO Info);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, Enums.AllocationType lAllocationType, Enums.MemoryProtection flProtect);

			[DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
			public static extern bool VirtualFree(IntPtr lpAddress,
				uint dwSize, Enums.FreeType dwFreeType);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern bool VirtualProtect(IntPtr lpAddress, int dwSize,
				Enums.MemoryProtection flNewProtect, out Enums.MemoryProtection lpflOldProtect);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern int VirtualQuery(IntPtr lpAddress, out Structures.MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

			[DllImport("kernel32.dll")]
			public static extern bool AllocConsole();

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern IntPtr CreateFile(string lpFileName
				, [MarshalAs(UnmanagedType.U4)] Enums.DesiredAccess dwDesiredAccess
				, [MarshalAs(UnmanagedType.U4)] FileShare dwShareMode
				, uint lpSecurityAttributes
				, [MarshalAs(UnmanagedType.U4)] FileMode dwCreationDisposition
				, [MarshalAs(UnmanagedType.U4)] FileAttributes dwFlagsAndAttributes
				, uint hTemplateFile);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern bool SetStdHandle(Enums.StdHandle nStdHandle, IntPtr hHandle);

			[DllImport("kernel32.dll")]
			public static extern IntPtr OpenThread(Enums.ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

			[DllImport("kernel32.dll")]
			public static extern int SuspendThread(IntPtr hThread);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern int ResumeThread(IntPtr hThread);

			[DllImport("kernel32.dll", SetLastError = true)]
			[SuppressUnmanagedCodeSecurity]
			[return: MarshalAs(UnmanagedType.Bool)]
			public static extern bool CloseHandle(IntPtr hObject);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern void SetLastError(uint dwErrorCode);

			[DllImport("kernel32.dll", CharSet = CharSet.Auto)]
			public static extern IntPtr GetModuleHandle(string lpModuleName);

			[DllImport("user32.dll", EntryPoint = "FindWindow", SetLastError = true)]
			public static extern IntPtr FindWindowByCaption(IntPtr ZeroOnly, string lpWindowName);

			[DllImport("kernel32.dll", SetLastError = false)]
			public static extern IntPtr HeapAlloc(IntPtr hHeap, uint dwFlags, UIntPtr dwBytes);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern IntPtr HeapCreate(uint flOptions, UIntPtr dwInitialSize,
				UIntPtr dwMaximumSize);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern bool HeapFree(IntPtr hHeap, uint dwFlags, IntPtr lpMem);
		}
	}




	public class DebugConsole
	{
		public static bool InitiateDebugConsole()
		{
			if (Memory.PInvoke.AllocConsole())
			{
				//https://developercommunity.visualstudio.com/content/problem/12166/console-output-is-gone-in-vs2017-works-fine-when-d.html
				// Console.OpenStandardOutput eventually calls into GetStdHandle. As per MSDN documentation of GetStdHandle: http://msdn.microsoft.com/en-us/library/windows/desktop/ms683231(v=vs.85).aspx will return the redirected handle and not the allocated console:
				// "The standard handles of a process may be redirected by a call to  SetStdHandle, in which case  GetStdHandle returns the redirected handle. If the standard handles have been redirected, you can specify the CONIN$ value in a call to the CreateFile function to get a handle to a console's input buffer. Similarly, you can specify the CONOUT$ value to get a handle to a console's active screen buffer."
				// Get the handle to CONOUT$.    
				var stdOutHandle = Memory.PInvoke.CreateFile("CONOUT$", Memory.Enums.DesiredAccess.GenericRead | Memory.Enums.DesiredAccess.GenericWrite, FileShare.ReadWrite, 0, FileMode.Open, FileAttributes.Normal, 0);
				var stdInHandle = Memory.PInvoke.CreateFile("CONIN$", Memory.Enums.DesiredAccess.GenericRead | Memory.Enums.DesiredAccess.GenericWrite, FileShare.ReadWrite, 0, FileMode.Open, FileAttributes.Normal, 0);

				if (stdOutHandle == new IntPtr(-1))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}

				if (stdInHandle == new IntPtr(-1))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}


				if (!Memory.PInvoke.SetStdHandle(Memory.Enums.StdHandle.Output, stdOutHandle))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}

				if (!Memory.PInvoke.SetStdHandle(Memory.Enums.StdHandle.Input, stdInHandle))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}

				var standardOutput = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
				var standardInput = new StreamReader(Console.OpenStandardInput());

				Console.SetIn(standardInput);
				Console.SetOut(standardOutput);
				return true;
			}
			return false;
		}
	}

	public class HelperMethods
	{
		public static string TranslateAssembleType(Memory.Enums.AssembleType type)
		{
			switch (type)
			{
				case Memory.Enums.AssembleType.X86_16:
					return "x86-16";
				case Memory.Enums.AssembleType.X86_32:
					return "x86-32";
				case Memory.Enums.AssembleType.X86_64:
					return "x86-64";
				default:
					return "error";

			}
		}


		public static unsafe T* malloc<T>(T obj) where T : unmanaged
		{
			var ptr = (T*)Memory.Allocator.Unmanaged.Extended.AllocateHeap((uint) Marshal.SizeOf<T>());
			if (ptr == null)
				throw new Exception("malloc failed");

			*ptr = default;
			return ptr;
		} 

		public static void PrintExceptionData(object exceptionObj, bool writeToFile = false)
		{
			if (exceptionObj == null) return;
			Type actualType = exceptionObj.GetType();

			Exception exceptionObject = exceptionObj as Exception;

			var s = new StackTrace(exceptionObject);
			var thisasm = Assembly.GetExecutingAssembly();

			var methodName = s.GetFrames().Select(f => f.GetMethod()).First(m => m.Module.Assembly == thisasm).Name;
			var parameterInfo = s.GetFrames().Select(f => f.GetMethod()).First(m => m.Module.Assembly == thisasm).GetParameters();
			var methodReturnType = s.GetFrame(1).GetMethod().GetType();

			var lineNumber = s.GetFrame(0).GetFileLineNumber();

			// string formatedMethodNameAndParameters = $"{methodReturnType} {methodName}(";
			string formatedMethodNameAndParameters = $"{methodName}(";

			if (parameterInfo.Length < 1)
			{
				formatedMethodNameAndParameters += ")";
			}
			else
			{
				for (int n = 0; n < parameterInfo.Length; n++)
				{
					ParameterInfo param = parameterInfo[n];
					string parameterName = param.Name;

					if (n == parameterInfo.Length - 1)
						formatedMethodNameAndParameters += $"{param.ParameterType} {parameterName})";
					else
						formatedMethodNameAndParameters += $"{param.ParameterType} {parameterName},";
				}
			}

			string formattedContent = $"[UNHANDLED_EXCEPTION] Caught Exception of type {actualType}\n\n" +
									  $"Exception Message: {exceptionObject.Message}\n" +
									  $"Exception Origin File/Module: {exceptionObject.Source}\n" +
									  $"Method that threw the Exception: {formatedMethodNameAndParameters}\n" +
									  $"Line Number: {lineNumber}\n";

			Console.WriteLine(formattedContent);

			if (exceptionObject.Data.Count > 0)
			{
				Console.WriteLine($"Exception Data Dictionary Results:");
				foreach (DictionaryEntry pair in exceptionObject.Data)
					Console.WriteLine("	* {0} = {1}", pair.Key, pair.Value);
			}

			if (writeToFile)
				WriteToFile(formattedContent);

		}

		public static uint RelAddr(uint from, uint to)
			=> to - from - 5;

		public static unsafe long FindPattern(byte* body, int bodyLength, byte[] pattern, byte[] masks, long start = 0)
		{
			long foundIndex = -1;

			if (bodyLength <= 0 || pattern.Length <= 0 || start > bodyLength - pattern.Length ||
			    pattern.Length > bodyLength) return foundIndex;

			for (long index = start; index <= bodyLength - pattern.Length; index++)
			{
				if (((body[index] & masks[0]) != (pattern[0] & masks[0]))) continue;

				var match = true;
				for (int index2 = 1; index2 <= pattern.Length - 1; index2++)
				{
					if ((body[index + index2] & masks[index2]) == (pattern[index2] & masks[index2])) continue;
					match = false;
					break;

				}

				if (!match) continue;

				foundIndex = index;
				break;
			}

			return foundIndex;
		}

		public static void WriteToFile(string contents)
		{
			if (contents.Length < 1) return;
			try
			{
				File.WriteAllText($"{Memory.HostProcess.ProcessName}_SessionLogs.txt", contents);
			}
			catch
			{
				Debug.WriteLine($"WriteToFile - Failed writing contents to file '{Memory.HostProcess.ProcessName}_SessionLogs.txt'");
			}
		}
	}

	// Extensions
	public static class ProcessExtensions
	{
		public static ProcessModule FindProcessModule(this Process obj, string moduleName)
		{
			foreach (ProcessModule pm in obj.Modules)
				if (string.Equals(pm.ModuleName, moduleName, StringComparison.CurrentCultureIgnoreCase))
					return pm;

			return null;
		}
	}

	public static class ByteArrayExtensions
	{
		public static byte[] FromHexCE(this byte[] destination, string CEStyleHexString)
		{
			if (string.IsNullOrEmpty(CEStyleHexString))
				return null;
			// Expects CE styled string array

			string[] split = CEStyleHexString.TrimStart().TrimEnd().Split(' ');
			byte[] ret = new byte[split.Length];

			for (int n = 0; n < split.Length; n++)
			{
				ret[n] = Convert.ToByte(split[n], 16);
			}
				

			return ret;
		}

		public static void FromHexIDA(ref byte[] destination, string IStyleHexString)
		{
			if (string.IsNullOrEmpty(IStyleHexString))
				return;
			// Expects CE styled string array

			string[] split = IStyleHexString.TrimStart().TrimEnd().Split(new [] { "\\x" }, StringSplitOptions.None);
			byte[] ret = new byte[split.Length];

			for (int n = 0; n < split.Length; n++)
			{
				ret[n] = Convert.ToByte(split[n], 16);
			}

			destination = ret;
		}

		public static string ByteArrayToHexString(this byte[] obj, bool CEStyleString = true)
		{
			string repres = "";
			if (CEStyleString)
			{
				foreach (var bt in obj)
				{
					repres += $"{bt:X2} ";
				}
				return repres.TrimEnd(' ');
			}
			else
			{
				foreach (var bt in obj)
				{
					repres += $"\\x{bt:X2}";
				}
				return repres;
			}
		}
	}

	public static class GraphicsExtensions
	{
		public static void DrawBoundingBox(this Graphics gObj, Memory.Structures.Vector headScreenPos, Memory.Structures.Vector feetScreenPos, Pen color)
		{
			if (gObj == null)
				return;

			const int OFFSET = 20;
			float height = Math.Abs(headScreenPos.Y - feetScreenPos.Y);
			float width = height / 2;

			gObj.DrawRectangle(color,
				headScreenPos.X - width / 2, headScreenPos.Y - OFFSET,
				width, height + OFFSET);
		}
	}
}
