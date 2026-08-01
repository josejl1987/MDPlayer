using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fmp.Core.Nise98
{
    public interface iEncoding
    {
        string GetStringFromSjisArray(byte[] sjisArray);

        string GetStringFromSjisArray(byte[] sjisArray, int index, int count);

        byte[] GetSjisArrayFromString(string utfString);

        string GetStringFromUtfArray(byte[] utfArray);

        byte[] GetUtfArrayFromString(string utfString);

    }
}
