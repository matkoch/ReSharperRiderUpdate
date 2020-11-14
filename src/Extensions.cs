using System;
using System.Linq;
using System.Reflection;
using Nuke.Common.Execution;

public static class Extensions
{
    public static object GetValue(
        this Type type,
        string memberName,
        object target,
        BindingFlags? bindingFlags = null)
    {
        var members = type.GetMember(memberName, bindingFlags ?? (target != null ? ReflectionService.Instance : ReflectionService.Static));
        var member = members.FirstOrDefault();
        return member switch
        {
            PropertyInfo propertyInfo => propertyInfo.GetValue(target),
            FieldInfo fieldInfo => fieldInfo.GetValue(target),
            _ => throw new NotSupportedException()
        };
    }

    public static T GetValue<T>(
        this Type type,
        string memberName,
        object target,
        BindingFlags? bindingFlags = null,
        params object[] args)
    {
        return (T) type.GetValue(memberName, target, bindingFlags);
    }
}