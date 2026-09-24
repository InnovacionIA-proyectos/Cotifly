using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>Atajos de Fluent API para mapear columnas existentes (Database First, ADR-008).</summary>
internal static class clsExtensionesMapeo
{
    /// <summary>Nombre y tipo SQL explícitos de la columna, tal como están en el script de esquema.</summary>
    public static PropertyBuilder<TPropiedad> Columna<TPropiedad>(
        this PropertyBuilder<TPropiedad> propiedad, string nombre, string tipo) =>
        propiedad.HasColumnName(nombre).HasColumnType(tipo);

    /// <summary><c>DATETIMEOFFSET</c> (precisión por defecto, 7).</summary>
    public static PropertyBuilder<TPropiedad> Fecha<TPropiedad>(this PropertyBuilder<TPropiedad> propiedad, string nombre) =>
        propiedad.Columna(nombre, clsCotiflyDbContext.TipoFecha);
}
