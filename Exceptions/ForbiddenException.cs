using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BCXAPI.Exceptions
{
    public class ForbiddenException : GeneralAPIException
    {

        public ForbiddenException()
            : base("You do not have access to perform this action or your accout limit has been reached.",403)
        {
           
        }
    }
}
