using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Asigna las marcas de tiempo con el reloj de la aplicación (<see cref="IClsReloj"/>) antes de guardar.
/// El script 0001 no crea triggers: <c>updatedAt</c> lo debe fijar quien escribe (§5.1 del diseño).
/// <list type="bullet">
/// <item>Filas nuevas: <c>CreatedAt</c> (si la entidad no trae uno) y <c>UpdatedAt</c>.</item>
/// <item>Filas modificadas: <c>UpdatedAt</c>.</item>
/// <item>Camiones nuevos (HU-021.5): <c>CreatedAt</c> + <see cref="clsVehiculoLineaSolicitud.OrdenCreacion"/> ×
/// <see cref="clsVehiculoLineaSolicitud.DesfaseCreacion"/>, para que los camiones de una ruta creados juntos se lean en el orden de la
/// recomendación del cubicador (el esquema no tiene columna de orden; deuda para un script ≥ 0011).</item>
/// </list>
/// Aplica a toda entidad que tenga esas propiedades (se detectan en el modelo EF, sin interfaces en Dominio).
/// </summary>
public sealed class clsMarcasTiempoInterceptor(IClsReloj reloj) : SaveChangesInterceptor
{
    public const string PropiedadCreatedAt = "CreatedAt";
    public const string PropiedadUpdatedAt = "UpdatedAt";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Aplicar(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Aplicar(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Aplicar(DbContext? contexto)
    {
        if (contexto is null)
        {
            return;
        }

        var ahora = reloj.AhoraUtc;
        foreach (var entrada in contexto.ChangeTracker.Entries())
        {
            switch (entrada.State)
            {
                case EntityState.Added:
                    AsignarSiVacio(entrada, PropiedadCreatedAt,
                        entrada.Entity is clsVehiculoLineaSolicitud camion ? ahora + (camion.OrdenCreacion * clsVehiculoLineaSolicitud.DesfaseCreacion) : ahora);
                    Asignar(entrada, PropiedadUpdatedAt, ahora);
                    break;
                case EntityState.Modified:
                    Asignar(entrada, PropiedadUpdatedAt, ahora);
                    break;
                default:
                    break;
            }
        }
    }

    private static void Asignar(EntityEntry entrada, string nombre, DateTimeOffset ahora)
    {
        if (entrada.Metadata.FindProperty(nombre) is { } propiedad && propiedad.ClrType == typeof(DateTimeOffset))
        {
            entrada.Property(nombre).CurrentValue = ahora;
        }
    }

    private static void AsignarSiVacio(EntityEntry entrada, string nombre, DateTimeOffset ahora)
    {
        if (entrada.Metadata.FindProperty(nombre) is { } propiedad
            && propiedad.ClrType == typeof(DateTimeOffset)
            && entrada.Property(nombre).CurrentValue is DateTimeOffset actual
            && actual == default)
        {
            entrada.Property(nombre).CurrentValue = ahora;
        }
    }
}
