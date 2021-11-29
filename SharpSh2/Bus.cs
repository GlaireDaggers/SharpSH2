using System;
using System.Collections.Generic;
using System.Text;

namespace SharpSh2
{
	/// <summary>
	/// Generic implementation of a memory bus
	/// </summary>
	public interface IBus
	{
		byte Read8(uint addr);
		ushort Read16(uint addr);
		uint Read32(uint addr);
		void Write8(uint addr, byte value);
		void Write16(uint addr, ushort value);
		void Write32(uint addr, uint value);
	}
}
