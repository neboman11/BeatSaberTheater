using System;
using System.Collections.Generic;
using System.Linq;

namespace BeatSaberTheater.Settings;

public static class VideoFormats
{
	public enum Format
	{
		Mp4 = 0,
		Webm = 1,
	}

	public static string ToName(Format format)
	{
		return Enum.GetName(typeof(Format), format);
	}

	public static List<object> GetFormatList()
	{
		var enumArray = Enum.GetValues(typeof(Format));
		var enumArrayFormatted = new object[enumArray.Length];
		for (var i = 0; i < enumArray.Length; i++) enumArrayFormatted[i] = ToName((Format)enumArray.GetValue(i));
		return enumArrayFormatted.ToList();
	}

	public static Format FromName(string mode)
	{
		return Enum.Parse<Format>(mode);
	}
}