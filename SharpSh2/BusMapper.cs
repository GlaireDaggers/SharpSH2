using System;
using System.Collections.Generic;
using System.Text;

namespace SharpSh2
{
	/// <summary>
	/// Maps multiple IBus devices to a single address space
	/// </summary>
	public class SimpleBusMapper : IBus
	{
		#region Types

		private struct Range
		{
			public uint start;
			public uint end;

			public bool Overlaps(Range other)
			{
				return start < other.end && other.start < end;
			}
		}

		#endregion

		#region Fields

		private List<KeyValuePair<Range, IBus>> _map;

		#endregion

		#region Constructor

		public SimpleBusMapper()
		{
			_map = new List<KeyValuePair<Range, IBus>>();
		}

		#endregion

		#region API

		/// <summary>
		/// Map a new bus device to the given address range
		/// </summary>
		public void Map(IBus child, uint start, uint len)
		{
			Range r = new Range()
			{
				start = start,
				end = start + len,
			};

			// check for overlaps
			foreach (var device in _map)
			{
				if (device.Key.Overlaps(r))
				{
					throw new ArgumentOutOfRangeException($"{nameof(start)}+{nameof(len)} overlaps another range already defined");
				}
			}

			_map.Add(new KeyValuePair<Range, IBus>(r, child));
		}

		public IBus GetDeviceAt(uint addr, out uint startaddr, out uint endaddr)
		{
			foreach (var device in _map)
			{
				if (addr >= device.Key.start && addr < device.Key.end)
				{
					startaddr = device.Key.start;
					endaddr = device.Key.end;
					return device.Value;
				}
			}

			startaddr = 0;
			endaddr = 0;
			return null;
		}

		#endregion

		#region IBus

		public ushort Read16(uint addr)
		{
			return GetDeviceAt(addr, out uint start, out uint _)?.Read16(addr - start) ?? 0;
		}

		public uint Read32(uint addr)
		{
			return GetDeviceAt(addr, out uint start, out uint _)?.Read32(addr - start) ?? 0;
		}

		public byte Read8(uint addr)
		{
			return GetDeviceAt(addr, out uint start, out uint _)?.Read8(addr - start) ?? 0;
		}

		public void Write16(uint addr, ushort value)
		{
			GetDeviceAt(addr, out uint start, out uint _)?.Write16(addr - start, value);
		}

		public void Write32(uint addr, uint value)
		{
			GetDeviceAt(addr, out uint start, out uint _)?.Write32(addr - start, value);
		}

		public void Write8(uint addr, byte value)
		{
			GetDeviceAt(addr, out uint start, out uint _)?.Write8(addr - start, value);
		}

		#endregion
	}
}
