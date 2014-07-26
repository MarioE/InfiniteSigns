using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InfiniteSigns
{
	public enum SignAction : byte
	{
		None,
		Protect,
		Unprotect,
		Info,
		SetPassword,
		TogglePublic,
		ToggleRegion,
	}
}
