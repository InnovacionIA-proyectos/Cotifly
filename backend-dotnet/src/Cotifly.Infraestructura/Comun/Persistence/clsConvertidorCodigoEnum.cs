using Cotifly.Dominio.Comun;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Guarda un enum del dominio como su código textual (<see cref="clsCodigoEnum"/>): <c>sin_enviar</c>,
/// <c>politica_linea</c>, <c>gerente_comercial</c>... Son los valores que exigen los CHECK del esquema.
/// </summary>
public sealed class clsConvertidorCodigoEnum<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public clsConvertidorCodigoEnum()
        : base(valor => clsCodigoEnum.ACodigo(valor), codigo => clsCodigoEnum.DesdeCodigo<TEnum>(codigo))
    {
    }
}
