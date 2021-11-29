using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Reflection;

namespace SharpSh2.Test
{
	public static class Utils
	{
		public static byte[] LoadEmbeddedRom(string path)
		{
			string realPath = $"{nameof(SharpSh2)}.{nameof(Test)}.{path}";
			using (var reader = new BinaryReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(realPath)))
			{
				int len = (int)reader.BaseStream.Length;
				return reader.ReadBytes(len);
			}
		}
	}
}
