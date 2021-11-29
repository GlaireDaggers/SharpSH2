using System;
using System.Collections.Generic;
using System.Text;

namespace SharpSh2
{
	/// <summary>
	/// Implementation of IBus for a simple linear segment of read/write memory
	/// </summary>
	public unsafe class LinearMemory : IBus
	{
		#region Fields

		public readonly byte[] data;

		#endregion

		#region Constructor

		public LinearMemory(int size)
		{
			data = new byte[size + 4];
		}

		#endregion

		#region IBus

		public ushort Read16(uint addr)
		{
			fixed (byte* ptr = &data[addr % data.Length])
			{
				return *(ushort*)ptr;
			}
		}

		public uint Read32(uint addr)
		{
			fixed (byte* ptr = &data[addr % data.Length])
			{
				return *(uint*)ptr;
			}
		}

		public byte Read8(uint addr)
		{
			fixed (byte* ptr = &data[addr % data.Length])
			{
				return *ptr;
			}
		}

		public void Write16(uint addr, ushort value)
		{
			fixed (byte* ptr = &data[addr % data.Length])
			{
				*(ushort*)ptr = value;
			}
		}

		public void Write32(uint addr, uint value)
		{
			fixed (byte* ptr = &data[addr % data.Length])
			{
				*(uint*)ptr = value;
			}
		}

		public void Write8(uint addr, byte value)
		{
			fixed (byte* ptr = &data[addr % data.Length])
			{
				*ptr = value;
			}
		}

		#endregion
	}

	/// <summary>
	/// Implementation of IBus for a simple linear segment of read-only memory
	/// </summary>
	public unsafe class LinearReadOnlyMemory : IBus
	{
		#region Fields

		public readonly byte[] data;

		#endregion

		#region Constructor

		public LinearReadOnlyMemory(byte[] data)
		{
			this.data = new byte[data.Length + 4];
			Buffer.BlockCopy(data, 0, this.data, 0, data.Length);
		}

		#endregion

		#region IBus

		public ushort Read16(uint addr)
		{
			fixed (byte* ptr = &data[addr % data.Length])
			{
				return *(ushort*)ptr;
			}
		}

		public uint Read32(uint addr)
		{
			fixed (byte* ptr = &data[addr % data.Length])
			{
				return *(uint*)ptr;
			}
		}

		public byte Read8(uint addr)
		{
			fixed (byte* ptr = &data[addr % data.Length])
			{
				return *ptr;
			}
		}

		public void Write16(uint addr, ushort value)
		{
		}

		public void Write32(uint addr, uint value)
		{
		}

		public void Write8(uint addr, byte value)
		{
		}

		#endregion
	}
}
