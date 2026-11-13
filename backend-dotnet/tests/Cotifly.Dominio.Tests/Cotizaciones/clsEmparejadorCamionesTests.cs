using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Emparejamiento de los camiones de Cotifly con las líneas de la solicitud de Nexo: N camiones por ruta, líneas en
/// cualquier orden, empates de camiones idénticos de una ruta resueltos en orden estable, la Mesa que le pone vehículo a una línea vacía
/// y la referencia de ruta de Nexo (N1) cuando exista.
/// </summary>
public sealed class clsEmparejadorCamionesTests
{
    [Fact]
    public void UnaLineaPorCamionLlevaLosDatosDeSuRutaYDeSuCamion()
    {
        var (cotizacion, ruta) = Ruta(("3S3", "FURGON", 10_000m), ("2S2", null, 8_000m), (null, null, 4_000m));

        var lineas = cotizacion.LineasNexo();

        lineas.Should().HaveCount(3, "Nexo cotiza cada camión como una línea");
        lineas.Select(l => l.Camion).Should().Equal(cotizacion.Vehiculos, "viajan en el orden estable de los camiones");
        lineas.Should().OnlyContain(l => l.Linea.Origen == "Buenaventura" && l.Linea.Destino == "Bogotá, D.C." && l.Linea.OrigenMunicipioId == 76109
                                         && l.Linea.Tipologia == "Contenedor" && l.Linea.ClaseViaje == "Contenedor" && l.Linea.HorasCargue == 6m
                                         && l.Linea.ValorMercancia == 90_000_000m && l.Linea.RutaId == ruta.Id && l.Linea.Posiciones.Count == 0);
        lineas.Select(l => (l.Linea.TipoVehiculo, l.Linea.Carroceria, l.Linea.PesoKg))
            .Should().Equal(lineas.Select(l => (l.Camion.TipoVehiculo, l.Camion.Carroceria, l.Camion.PesoKg)));
        lineas.Should().ContainSingle(l => l.Linea.TipoVehiculo == null && l.Linea.Carroceria == null,
            "el camión sin selección automática viaja igual, vacío, y lo resuelve la Mesa");
    }

    [Fact]
    public void NCamionesDeUnaRutaSeAsocianPorPesoYVehiculoEnCualquierOrden()
    {
        var (cotizacion, ruta) = Ruta(("3S3", "FURGON", 10_000m), ("2S2", null, 8_000m), (null, null, 4_000m));
        var camiones = Camiones(cotizacion);
        clsLineaSolicitudNexo[] nexo =
        [
            Linea("N-c", ruta, 4_000m, null, null),
            Linea("N-a", ruta, 10_000m, "3S3", "FURGON"),
            Linea("N-b", ruta, 8_000m, "2S2", null),
        ];

        foreach (var orden in Permutaciones(nexo))
        {
            var pares = clsEmparejadorLineasNexo.Emparejar(camiones, orden);

            pares.Should().HaveCount(3);
            pares[Camion(cotizacion, 10_000m).Id].Should().Be("N-a");
            pares[Camion(cotizacion, 8_000m).Id].Should().Be("N-b");
            pares[Camion(cotizacion, 4_000m).Id].Should().Be("N-c");
        }
    }

    [Fact]
    public void LosCamionesIdenticosDeUnaRutaSeAsignanEnOrdenEstable()
    {
        var (cotizacion, ruta) = Ruta(("3S3", "FURGON", 10_000m), ("3S3", "FURGON", 10_000m), ("2S2", null, 8_000m));
        var camiones = Camiones(cotizacion);
        clsLineaSolicitudNexo[] nexo = [Linea("N-z", ruta, 10_000m, "3S3", "FURGON"), Linea("N-2s2", ruta, 8_000m, "2S2", null), Linea("N-m", ruta, 10_000m, "3S3", "FURGON")];
        var identicos = camiones.Where(c => c.PesoKg == 10_000m).ToList();

        foreach (var orden in Permutaciones(nexo))
        {
            var pares = clsEmparejadorLineasNexo.Emparejar(camiones, orden);

            pares.Should().HaveCount(3, "dos camiones idénticos de la misma ruta son intercambiables: no se dejan sin asociar");
            pares[identicos[0].Id].Should().Be("N-m", "el primero en el orden de envío toma la primera línea por id de Nexo");
            pares[identicos[1].Id].Should().Be("N-z");
            pares[Camion(cotizacion, 8_000m).Id].Should().Be("N-2s2");
        }
    }

    [Fact]
    public void ElEmpateSeResuelveAunqueNexoNoTraigaElVehiculo()
    {
        var (cotizacion, ruta) = Ruta(("3S3", null, 10_000m), ("3S3", null, 10_000m));
        var camiones = Camiones(cotizacion);

        var pares = clsEmparejadorLineasNexo.Emparejar(camiones, [Linea("N2", ruta, 10_000m, null, null), Linea("N1", ruta, 10_000m, null, null)]);

        pares.Values.Should().BeEquivalentTo(new[] { "N1", "N2" });
        pares[camiones[0].Id].Should().Be("N1");
    }

    [Fact]
    public void SiLaMesaLePusoVehiculoALaLineaVaciaElCamionSeAsociaIgual()
    {
        var (cotizacion, ruta) = Ruta(("3S3", "FURGON", 10_000m), (null, null, 4_000m));
        var camiones = Camiones(cotizacion);

        var pares = clsEmparejadorLineasNexo.Emparejar(camiones, [Linea("N-mesa", ruta, 4_000m, "2", "ESTACAS"), Linea("N-3s3", ruta, 10_000m, "3S3", "FURGON")]);

        pares[Camion(cotizacion, 4_000m).Id].Should().Be("N-mesa", "un dato que solo trae Nexo no contradice al camión vacío");
        pares[Camion(cotizacion, 10_000m).Id].Should().Be("N-3s3");
    }

    [Fact]
    public void UnEmpateQueOtroCamionPuedeReclamarNoSeAdivina()
    {
        // El camión vacío de 10 t y los dos 3S3 de 10 t pueden ser cualquiera de las tres líneas 3S3: no se adivina cuál es el vacío.
        var (cotizacion, ruta) = Ruta(("3S3", null, 10_000m), ("3S3", null, 10_000m), (null, null, 10_000m));
        var camiones = Camiones(cotizacion);

        var pares = clsEmparejadorLineasNexo.Emparejar(
            camiones, [Linea("N1", ruta, 10_000m, "3S3", null), Linea("N2", ruta, 10_000m, "3S3", null), Linea("N3", ruta, 10_000m, "3S3", null)]);

        pares.Should().BeEmpty();
    }

    [Fact]
    public void CamionesIgualesDeRutasDistintasNoSeAsignanPorOrden()
    {
        var cotizacion = Solicitud(2);
        var camiones = Camiones(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;

        clsEmparejadorLineasNexo.Emparejar(camiones, [Linea("N1", rutas[0], 10_000m, "3S3", null), Linea("N2", rutas[1], 10_000m, "3S3", null)])
            .Should().BeEmpty("dos rutas con el mismo contenido pueden costar distinto por datos que no viajan en la línea");
    }

    [Fact]
    public void ConLaReferenciaDeRutaDeNexoLasRutasIgualesSeDistinguen()
    {
        var cotizacion = Solicitud(2);
        var camiones = Camiones(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;

        var pares = clsEmparejadorLineasNexo.Emparejar(camiones,
        [
            Linea("N1", rutas[1], 10_000m, "3S3", null) with { GrupoRutaId = rutas[1].Id.ToString("D") },
            Linea("N2", rutas[0], 10_000m, "3S3", null) with { GrupoRutaId = rutas[0].Id.ToString("D").ToUpperInvariant() },
        ]);

        pares[rutas[0].Vehiculos[0].Id].Should().Be("N2", "N1 de Nexo: la referencia de la ruta decide");
        pares[rutas[1].Vehiculos[0].Id].Should().Be("N1");
    }

    [Fact]
    public void UnaReferenciaDeRutaDistintaNuncaCoincide()
    {
        var (cotizacion, ruta) = Ruta(("3S3", null, 10_000m));
        var camion = Camiones(cotizacion)[0];

        clsEmparejadorLineasNexo.Coincide(camion, Linea("N1", ruta, 10_000m, "3S3", null) with { GrupoRutaId = Guid.NewGuid().ToString() }).Should().BeFalse();
        clsEmparejadorLineasNexo.Coincide(camion, Linea("N1", ruta, 10_000m, "3S3", null) with { Ruta = null, GrupoRutaId = ruta.Id.ToString() })
            .Should().BeTrue("con la referencia no hace falta el texto de la ruta");
    }

    [Fact]
    public void LasLineasYaAsignadasNoSeReasignan()
    {
        var (cotizacion, ruta) = Ruta(("3S3", null, 10_000m), ("3S3", null, 10_000m));
        var camiones = Camiones(cotizacion);

        var pares = clsEmparejadorLineasNexo.Emparejar([camiones[1]], [Linea("N1", ruta, 10_000m, null, null), Linea("N2", ruta, 10_000m, null, null)],
            new HashSet<string> { "N1" });

        pares.Should().HaveCount(1);
        pares[camiones[1].Id].Should().Be("N2", "N1 ya tiene dueño");
    }

    [Fact]
    public void UnaLineaHuerfanaDeOtraRutaIgualNoSeLaLlevaElCamionDeLaOtraRuta()
    {
        // La ruta 1 tenía un 2S2 de 8 t que se descartó antes de asociarse, así que su línea quedó huérfana en
        // Nexo; la ruta 2 (mismo origen y destino) tiene un 2S2 de 8 t y no se queda con la primera por id (la de la ruta 1).
        var cotizacion = Solicitud(2);
        var rutas = cotizacion.RutasOrdenadas;
        var deLaRuta2 = clsCamionParaEmparejar.Desde(rutas[1], rutas[1].Vehiculos[0]);
        clsLineaSolicitudNexo[] nexo = [Linea("A-huerfana-ruta1", rutas[0], 10_000m, "3S3", null), Linea("B-ruta2", rutas[1], 10_000m, "3S3", null)];

        var emparejamiento = clsEmparejadorLineasNexo.EmparejarConDetalle([deLaRuta2], nexo);

        emparejamiento.Pares.Should().BeEmpty("con una candidata de más no se sabe cuál es la suya: se avisa en vez de cruzar el costo");
        emparejamiento.EmpatesSinResolver.Should().ContainSingle().Which.Should().Contain("2 líneas candidatas");
        emparejamiento.LineasSinDueno.Should().BeEquivalentTo(new[] { "A-huerfana-ruta1", "B-ruta2" });
        emparejamiento.CamionesSinAsociar.Should().Equal(deLaRuta2.Id);

        // Con la referencia de ruta de Nexo (N1) sí se sabe.
        var conReferencia = clsEmparejadorLineasNexo.EmparejarConDetalle([deLaRuta2],
            [nexo[0] with { GrupoRutaId = rutas[0].Id.ToString() }, nexo[1] with { GrupoRutaId = rutas[1].Id.ToString() }]);
        conReferencia.Pares[deLaRuta2.Id].Should().Be("B-ruta2");
        conReferencia.LineasSinDueno.Should().Equal("A-huerfana-ruta1");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void FuzzConLineasHuerfanasNuncaCruzaUnaLineaDeOtraRuta(int semilla)
    {
        // Rutas con el MISMO origen y destino, camiones de pocos tipos (muchos empates) y algunos descartados antes de asociarse, cuyas
        // líneas quedan huérfanas en Nexo. Cada línea sabe de qué ruta salió: ningún camión puede quedarse con una de otra ruta.
        var azar = new Random(semilla);
        var cruces = 0;
        var asociados = 0;
        for (var vuelta = 0; vuelta < 200; vuelta++)
        {
            var tipos = new (string? Vehiculo, decimal Peso)[] { ("3S3", 10_000m), ("2S2", 8_000m), (null, 8_000m) };
            var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-FUZZ" }).Valor;
            var numeroRutas = azar.Next(1, 4);
            cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
                [.. Enumerable.Range(0, numeroRutas).Select(_ =>
                {
                    var tipo = tipos[azar.Next(tipos.Length)];
                    return LineaCaptura((tipo.Vehiculo, null, tipo.Peso));
                })],
                [.. Enumerable.Range(0, numeroRutas).Select(i => (int?)i)], 3);
            foreach (var ruta in cotizacion.RutasOrdenadas)
            {
                for (var extra = azar.Next(0, 4); extra > 0; extra--)
                {
                    var tipo = tipos[azar.Next(tipos.Length)];
                    ruta.AgregarVehiculo(tipo.Vehiculo, null, tipo.Peso);
                }
            }

            var dueno = new Dictionary<string, Guid>();
            var lineas = new List<clsLineaSolicitudNexo>();
            var vigentes = new List<clsCamionParaEmparejar>();
            foreach (var ruta in cotizacion.RutasOrdenadas)
            {
                foreach (var camion in ruta.VehiculosOrdenados)
                {
                    var id = Guid.NewGuid().ToString();
                    dueno[id] = ruta.Id;
                    lineas.Add(Linea(id, ruta, camion.PesoKg!.Value, camion.TipoVehiculo, null));
                    if (azar.Next(4) != 0)
                    {
                        vigentes.Add(clsCamionParaEmparejar.Desde(ruta, camion)); // uno de cada cuatro se descarta: su línea queda huérfana
                    }
                }
            }

            var barajadas = lineas.OrderBy(_ => azar.Next()).ToList();
            var pares = clsEmparejadorLineasNexo.EmparejarConDetalle(vigentes, barajadas).Pares;
            asociados += pares.Count;
            cruces += pares.Count(p => dueno[p.Value] != vigentes.Single(c => c.Id == p.Key).RutaId);
        }

        cruces.Should().Be(0);
        asociados.Should().BeGreaterThan(0, "el fuzz también asocia");
    }

    // ------------------------------------------------------------------ Datos del cubicador en Nexo

    private static readonly Guid _recomendacionA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid _recomendacionB = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    [Fact]
    public void DosCamionesIdenticosConGrupoRutaEIdRecomendacionSeAsocianPorEsosCampos()
    {
        // El caso frágil: dos camiones IGUALES en la misma ruta. Con el par (grupoRutaId, idRecomendacionVehiculo) en el GET, cada línea
        // va a SU camión aunque el orden estable dijera lo contrario (N-a iría al primero) y aunque la Mesa le cambie el vehículo.
        var (cotizacion, ruta) = RutaCubicada(20_000m,
            new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionA), new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionB));
        var camiones = Camiones(cotizacion);
        clsLineaSolicitudNexo[] nexo =
        [
            Linea("N-a", ruta, 10_000m, "3S3", "FURGON") with { GrupoRutaId = ruta.Id.ToString("D"), IdRecomendacionVehiculo = _recomendacionB.ToString("D") },
            Linea("N-z", ruta, 10_000m, "2S2", "ESTACAS") with { GrupoRutaId = ruta.Id.ToString("D"), IdRecomendacionVehiculo = _recomendacionA.ToString("D") },
        ];

        foreach (var orden in Permutaciones(nexo))
        {
            var emparejamiento = clsEmparejadorLineasNexo.EmparejarConDetalle(camiones, orden);

            emparejamiento.Pares.Should().HaveCount(2);
            emparejamiento.Pares[CamionDe(cotizacion, _recomendacionA).Id].Should().Be("N-z", "la línea dice de qué camión del cubicador es");
            emparejamiento.Pares[CamionDe(cotizacion, _recomendacionB).Id].Should().Be("N-a");
            emparejamiento.EmpatesSinResolver.Should().BeEmpty();
        }
    }

    [Fact]
    public void DosCamionesIdenticosSoloConGrupoRutaSeAsocianPorGrupoYLuegoPorContenido()
    {
        // Sin idRecomendacionVehiculo en el GET (Nexo:EnviarDatosCubicador apagada): el grupo separa la ruta de otra igual y el contenido
        // resuelve el empate en orden estable (los dos camiones valen lo mismo).
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-CUB-2" }).Valor;
        var camionesRuta = new[] { new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionA), new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionB) };
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
        [
            new clsRutaCapturada(LineaCaptura((null, null, 20_000m)), 3, 0, camionesRuta, []),
            new clsRutaCapturada(LineaCaptura((null, null, 20_000m)), 3, 1, [new clsCamionPlaneado("3S3", "FURGON", 10_000m), new clsCamionPlaneado("3S3", "FURGON", 10_000m)], []),
        ]);
        var rutas = cotizacion.RutasOrdenadas;
        var camiones = Camiones(cotizacion);
        var nexo = rutas.SelectMany((r, i) => new[]
        {
            Linea($"R{i}-2", r, 10_000m, "3S3", "FURGON") with { GrupoRutaId = r.Id.ToString("D") },
            Linea($"R{i}-1", r, 10_000m, "3S3", "FURGON") with { GrupoRutaId = r.Id.ToString("D") },
        }).Reverse().ToList();

        var emparejamiento = clsEmparejadorLineasNexo.EmparejarConDetalle(camiones, nexo);

        emparejamiento.Pares.Should().HaveCount(4, "el grupo distingue las dos rutas iguales");
        foreach (var (ruta, i) in rutas.Select((r, i) => (r, i)))
        {
            ruta.VehiculosOrdenados.Select(v => emparejamiento.Pares[v.Id]).Should().Equal($"R{i}-1", $"R{i}-2");
        }
    }

    [Fact]
    public void DosCamionesIdenticosSinLosCamposNuevosSiguenLaLogicaDeHoy()
    {
        var (cotizacion, ruta) = RutaCubicada(20_000m,
            new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionA), new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionB));

        var pares = clsEmparejadorLineasNexo.Emparejar(Camiones(cotizacion), [Linea("N-z", ruta, 10_000m, "3S3", "FURGON"), Linea("N-a", ruta, 10_000m, "3S3", "FURGON")]);

        pares[CamionDe(cotizacion, _recomendacionA).Id].Should().Be("N-a", "sin los campos nuevos: orden estable por id de Nexo");
        pares[CamionDe(cotizacion, _recomendacionB).Id].Should().Be("N-z");
    }

    [Fact]
    public void UnaLineaDeOtroCamionDelCubicadorNuncaCoincide()
    {
        var (cotizacion, ruta) = RutaCubicada(10_000m, new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionA));
        var camion = Camiones(cotizacion)[0];

        clsEmparejadorLineasNexo.Coincide(camion, Linea("N1", ruta, 10_000m, "3S3", "FURGON") with { IdRecomendacionVehiculo = _recomendacionB.ToString() })
            .Should().BeFalse();
        clsEmparejadorLineasNexo.Coincide(camion, Linea("N1", ruta, 10_000m, "3S3", "FURGON") with { IdRecomendacionVehiculo = _recomendacionA.ToString().ToUpperInvariant() })
            .Should().BeTrue();
    }

    [Fact]
    public void UnParRepetidoEnNexoNoSeUsaYSigueElContenido()
    {
        var (cotizacion, ruta) = RutaCubicada(20_000m,
            new clsCamionPlaneado("3S3", "FURGON", 12_000m, _recomendacionA), new clsCamionPlaneado("2S2", null, 8_000m, _recomendacionB));
        var par = (Grupo: ruta.Id.ToString("D"), Recomendacion: _recomendacionA.ToString("D"));

        var pares = clsEmparejadorLineasNexo.Emparejar(Camiones(cotizacion),
        [
            Linea("N1", ruta, 12_000m, "3S3", "FURGON") with { GrupoRutaId = par.Grupo, IdRecomendacionVehiculo = par.Recomendacion },
            Linea("N2", ruta, 8_000m, "2S2", null) with { GrupoRutaId = par.Grupo, IdRecomendacionVehiculo = par.Recomendacion },
        ]);

        pares.Should().ContainSingle().Which.Should().Be(new KeyValuePair<Guid, string>(CamionDe(cotizacion, _recomendacionA).Id, "N1"),
            "N2 dice ser del camión A pero contradice su contenido: no se asocia al B");
    }

    [Fact]
    public void ConUnSoloCamionLaLineaLlevaElPesoDeclaradoDeLaRutaYNoElDelCubicador()
    {
        // El cubicador dice 26 t (más de 1 % lejos de las posiciones, 28 500,5 kg); la línea lleva el peso de la ruta,
        // que cuadra con las posiciones, y así Nexo no responde PESO_INCONSISTENTE. El peso del cubicador se guarda en el camión.
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-CUB-3" }).Valor;
        var linea = LineaCaptura((null, null, 28_500.5m)) with
        {
            Posiciones = [new clsPosicionCarga("Estiba de café", 20, 120m, 100m, 150m, 1425m, null, "CAFE"), new clsPosicionCarga("Saco", 1, 50m, 40m, 30m, 0.5m, null, "SACO")],
        };
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
            [new clsRutaCapturada(linea, 3, 0, [new clsCamionPlaneado("3S3", "FURGON", 26_000m, _recomendacionA)], [])]);
        var ruta = cotizacion.RutasOrdenadas[0];
        var camion = ruta.Vehiculos[0];

        var (_, lineaNexo) = cotizacion.LineasNexo().Single();

        camion.PesoKg.Should().Be(26_000m, "el peso del cubicador se guarda y se muestra");
        lineaNexo.PesoKg.Should().Be(28_500.5m);
        Math.Abs(lineaNexo.PesoKg!.Value - lineaNexo.SumaPesoPosiciones).Should().BeLessThanOrEqualTo(lineaNexo.PesoKg.Value * 0.01m);
        lineaNexo.Errores().Should().NotContain(e => e.Codigo == clsCodigosLineaSolicitud.PesoInconsistente);
        lineaNexo.IdRecomendacionVehiculo.Should().Be(_recomendacionA);
        lineaNexo.Posiciones.Select(p => p.CodigoMercancia).Should().Equal("CAFE", "SACO");
        clsCamionParaEmparejar.Desde(ruta, camion).PesoKg.Should().Be(28_500.5m, "el emparejador compara con el peso que viajó");
        clsEmparejadorLineasNexo.Emparejar([clsCamionParaEmparejar.Desde(ruta, camion)], [Linea("N1", ruta, 28_500.5m, "3S3", "FURGON")])
            .Should().ContainKey(camion.Id, "Nexo devuelve el peso que recibió ");
    }

    [Fact]
    public void ConVariosCamionesCadaLineaLlevaElPesoDelCubicadorSinPosiciones()
    {
        var (cotizacion, _) = RutaCubicada(20_000m,
            new clsCamionPlaneado("3S3", "FURGON", 12_345m, _recomendacionA), new clsCamionPlaneado("2S2", null, 7_655m, _recomendacionB));

        var lineas = cotizacion.LineasNexo();

        lineas.Select(l => l.Linea.PesoKg).Should().Equal(12_345m, 7_655m);
        lineas.Should().OnlyContain(l => l.Linea.Posiciones.Count == 0);
        lineas.Select(l => l.Linea.IdRecomendacionVehiculo).Should().Equal(_recomendacionA, _recomendacionB);
    }

    [Theory]
    [InlineData(false, false, 0, true)]
    [InlineData(false, false, 1, true)]
    [InlineData(false, false, 2, true)]
    [InlineData(false, false, 3, true)]
    [InlineData(false, false, 0, false)]
    [InlineData(false, false, 1, false)]
    [InlineData(false, false, 2, false)]
    [InlineData(false, false, 3, false)]
    [InlineData(false, true, 3, false)]
    [InlineData(true, false, 3, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, true, 3, false)]
    [InlineData(true, true, 3, true)]
    // Contrato vigente (banderas encendidas por defecto): la línea suelta (ruta de un camión) viaja con grupoRutaId = null.
    [InlineData(true, true, 0, true, true)]
    [InlineData(true, true, 1, true, true)]
    [InlineData(true, true, 2, true, true)]
    [InlineData(true, true, 3, true, true)]
    [InlineData(true, false, 3, true, true)]
    public void FuzzConElCubicadorNoCruzaMasQueAntesYConGrupoRutaNuncaCruza(bool grupoEnGet, bool recomendacionEnGet, int modo, bool codigos, bool lineaSuelta = false)
    {
        // Con Nexo:EnviarCodigosCubicador apagada (codigos = false) la línea del camión del cubicador viaja sin vehículo ni
        // carrocería y el emparejador no los compara; Nexo devuelve numeroLinea (posición en el arreglo).
        // Rutas de 1 a 3 camiones del cubicador (pesos que no cuadran con la ruta), respaldo, la Mesa
        // que cambia el vehículo (modo & 1) o borra líneas antes del primer polling (modo & 2). Se compara contra lo que hacía QA (el peso
        // del camión en los dos lados): no puede cruzar más costos entre rutas que antes, y con grupoRutaId en el GET no cruza ninguno.
        var azar = new Random(159 + modo);
        var (crucesRama, crucesQa, pares) = (0, 0, 0);
        for (var vuelta = 0; vuelta < 1500; vuelta++)
        {
            var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-FUZZ-159" }).Valor;
            var rutas = new List<clsRutaCapturada>();
            for (var r = azar.Next(2, 4); r > 0; r--)
            {
                var peso = azar.Next(2) == 0 ? 20_000m : 28_500.5m;
                var linea = LineaCaptura((null, null, peso)) with { Posiciones = [new clsPosicionCarga("x", 1, 100m, 100m, 100m, peso, null, "COD")] };
                var cantidad = azar.Next(1, 4);
                IReadOnlyList<clsCamionPlaneado> camiones = azar.Next(5) == 0
                    ? [new clsCamionPlaneado(null, null, peso)]
                    : Enumerable.Range(0, cantidad).Select(_ => new clsCamionPlaneado(azar.Next(3) == 0 ? "2S2" : "3S3", "FURGON",
                        new[] { Math.Round(peso / cantidad, 2), 10_000m, peso, 18_000m }[azar.Next(4)], Guid.NewGuid())).ToList();
                rutas.Add(new clsRutaCapturada(linea, 3, 0, camiones, []));
            }

            cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot }, rutas);
            var lineas = new List<(clsLineaSolicitudNexo Rama, clsLineaSolicitudNexo Qa, Guid Ruta)>();
            var numero = 0;
            var camionesPorRuta = cotizacion.LineasNexo().GroupBy(x => x.Linea.RutaId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.Count());
            foreach (var (camion, linea) in cotizacion.LineasNexo())
            {
                numero++;
                var mesa = (modo & 1) != 0 && azar.Next(8) == 0 ? (azar.Next(2) == 0 ? "2S2" : "3S3") : null;
                if ((modo & 2) != 0 && azar.Next(10) == 0)
                {
                    continue;
                }

                var sinCodigos = !codigos && linea.IdRecomendacionVehiculo is not null;
                var rama = new clsLineaSolicitudNexo(Guid.NewGuid().ToString(), $"{linea.Origen} → {linea.Destino}", linea.Tipologia, linea.PesoKg,
                    mesa ?? (sinCodigos ? null : linea.TipoVehiculo), sinCodigos ? null : linea.Carroceria)
                {
                    GrupoRutaId = grupoEnGet && (!lineaSuelta || camionesPorRuta[linea.RutaId ?? Guid.Empty] > 1) ? linea.RutaId?.ToString("D") : null,
                    IdRecomendacionVehiculo = recomendacionEnGet ? linea.IdRecomendacionVehiculo?.ToString("D") : null,
                    NumeroLinea = grupoEnGet ? numero : null,
                };
                var qa = new clsLineaSolicitudNexo(rama.Id, rama.Ruta, linea.Tipologia, camion.PesoKg, mesa ?? linea.TipoVehiculo, linea.Carroceria);
                lineas.Add((rama, qa, camion.LineaSolicitudId));
            }

            lineas = [.. lineas.OrderBy(_ => azar.Next())];
            var rutaDe = cotizacion.RutasOrdenadas.SelectMany(r => r.Vehiculos.Select(v => (r, v))).ToDictionary(x => x.v.Id, x => x.r);
            var camionesRama = cotizacion.Vehiculos.Select(c => clsCamionParaEmparejar.Desde(rutaDe[c.Id], c, codigos)).ToList();
            var camionesQa = cotizacion.Vehiculos.Select(c => clsCamionParaEmparejar.Desde(rutaDe[c.Id], c) with { PesoKg = c.PesoKg, IdRecomendacionVehiculo = null }).ToList();

            foreach (var (camionId, lineaId) in clsEmparejadorLineasNexo.Emparejar(camionesRama, [.. lineas.Select(l => l.Rama)]))
            {
                pares++;
                crucesRama += lineas.Single(l => l.Rama.Id == lineaId).Ruta != rutaDe[camionId].Id ? 1 : 0;
            }

            foreach (var (camionId, lineaId) in clsEmparejadorLineasNexo.Emparejar(camionesQa, [.. lineas.Select(l => l.Qa)]))
            {
                crucesQa += lineas.Single(l => l.Qa.Id == lineaId).Ruta != rutaDe[camionId].Id ? 1 : 0;
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"FUZZ159 grupo={grupoEnGet} rec={recomendacionEnGet} modo={modo} codigos={codigos} suelta={lineaSuelta}: pares={pares} cruces={crucesRama} qa={crucesQa}");
        pares.Should().BeGreaterThan(0);
        crucesRama.Should().BeLessThanOrEqualTo(crucesQa, "no puede cruzar más costos entre rutas que QA");
        if (grupoEnGet && (!lineaSuelta || recomendacionEnGet))
        {
            // La línea suelta no trae grupoRutaId; la protege su idRecomendacionVehiculo. Sin él (bandera de datos del
            // cubicador apagada), la línea suelta se empareja por contenido como en QA: no cruza más que QA, pero puede cruzar.
            crucesRama.Should().Be(0, "con grupoRutaId (o, en la línea suelta, idRecomendacionVehiculo) en el GET ninguna línea va a otra ruta");
        }
    }

    [Fact]
    public void LaLineaSueltaSinGrupoRutaIdSeAsociaPorSuCamionDelCubicadorAunqueHayaOtraRutaIgual()
    {
        // Contrato con Nexo §3.3: una ruta de un solo camión viaja con grupoRutaId = null. Dos rutas iguales (mismo texto,
        // peso y vehículo), cada una con un camión: el idRecomendacionVehiculo del GET basta para no cruzarlas, en cualquier orden.
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-SUELTA-1" }).Valor;
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
        [
            new clsRutaCapturada(LineaCaptura((null, null, 10_000m)), 3, 0, [new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionA)], []),
            new clsRutaCapturada(LineaCaptura((null, null, 10_000m)), 3, 1, [new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionB)], []),
        ]);
        var rutas = cotizacion.RutasOrdenadas;
        clsLineaSolicitudNexo[] nexo =
        [
            Linea("N-1", rutas[0], 10_000m, "3S3", "FURGON") with { IdRecomendacionVehiculo = _recomendacionB.ToString("D").ToUpperInvariant(), NumeroLinea = 2 },
            Linea("N-2", rutas[1], 10_000m, "3S3", "FURGON") with { IdRecomendacionVehiculo = _recomendacionA.ToString("D").ToUpperInvariant(), NumeroLinea = 1 },
        ];

        foreach (var orden in Permutaciones(nexo))
        {
            var pares = clsEmparejadorLineasNexo.Emparejar(Camiones(cotizacion), orden);

            pares.Should().HaveCount(2);
            pares[CamionDe(cotizacion, _recomendacionA).Id].Should().Be("N-2", "el camión del cubicador (en MAYÚSCULAS) dice de qué ruta es");
            pares[CamionDe(cotizacion, _recomendacionB).Id].Should().Be("N-1");
        }
    }

    [Fact]
    public void LaLineaSueltaSinGrupoNiRecomendacionSeAsociaPorContenido()
    {
        // Un camión de respaldo (sin recomendación) en una ruta de un camión: grupoRutaId = null y sin idRecomendacionVehiculo;
        // se asocia por el texto de la ruta y el contenido.
        var (cotizacion, ruta) = RutaCubicada(10_000m, new clsCamionPlaneado(null, null, 10_000m));

        var pares = clsEmparejadorLineasNexo.Emparejar(Camiones(cotizacion), [Linea("N-1", ruta, 10_000m, null, null)]);

        pares.Should().ContainSingle().Which.Value.Should().Be("N-1");
    }

    [Fact]
    public void ConElGrupoRutaElNumeroDeLineaDeNexoDesempataLosCamionesIdenticos()
    {
        // numeroLinea es la posición en el arreglo del alta. Con el grupo, el orden del alta manda sobre el id de Nexo.
        var (cotizacion, ruta) = RutaCubicada(20_000m,
            new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionA), new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionB));
        var grupo = ruta.Id.ToString("D");

        var pares = clsEmparejadorLineasNexo.Emparejar(Camiones(cotizacion),
        [
            Linea("N-a", ruta, 10_000m, "3S3", "FURGON") with { GrupoRutaId = grupo, NumeroLinea = 2 },
            Linea("N-z", ruta, 10_000m, "3S3", "FURGON") with { GrupoRutaId = grupo, NumeroLinea = 1 },
        ]);

        pares[CamionDe(cotizacion, _recomendacionA).Id].Should().Be("N-z", "el primer camión viajó como la línea 1");
        pares[CamionDe(cotizacion, _recomendacionB).Id].Should().Be("N-a");
    }

    [Fact]
    public void SinLosCodigosDelCubicadorElCamionSeEmparejaSinVehiculoYLaMesaNoLoContradice()
    {
        // Nexo:EnviarCodigosCubicador apagada: la línea viajó vacía y la Mesa le puso otro vehículo; el camión del cubicador sigue
        // coincidiendo. El camión del respaldo con vehículo digitado (sin recomendación) sí se compara con su vehículo.
        var (cotizacion, ruta) = RutaCubicada(10_000m, new clsCamionPlaneado("3S3", "FURGON", 10_000m, _recomendacionA));
        var camion = cotizacion.Vehiculos[0];

        var sinCodigos = clsCamionParaEmparejar.Desde(ruta, camion, codigosCubicador: false);

        (sinCodigos.TipoVehiculo, sinCodigos.Carroceria).Should().Be((null, null));
        clsEmparejadorLineasNexo.Coincide(sinCodigos, Linea("N1", ruta, 10_000m, "2S2", "ESTACAS")).Should().BeTrue();
        clsEmparejadorLineasNexo.Coincide(clsCamionParaEmparejar.Desde(ruta, camion), Linea("N1", ruta, 10_000m, "2S2", "ESTACAS")).Should().BeFalse();
        var (respaldo, rutaRespaldo) = RutaCubicada(10_000m, new clsCamionPlaneado("3S3", "PORTACONTENEDORES", 10_000m));
        clsCamionParaEmparejar.Desde(rutaRespaldo, respaldo.Vehiculos[0], codigosCubicador: false).TipoVehiculo.Should().Be("3S3",
            "el vehículo digitado del respaldo (Captura:AceptarVehiculoCliente) es del catálogo de Nexo y sí viaja");
    }

    /// <summary>Ruta capturada con el cubicador: peso declarado <paramref name="pesoRuta"/> y los camiones del plan, en ese orden.</summary>
    private static (clsCotizacionSolicitud Cotizacion, clsLineaSolicitud Ruta) RutaCubicada(decimal pesoRuta, params clsCamionPlaneado[] camiones)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-CUB-1" }).Valor;
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
            [new clsRutaCapturada(LineaCaptura((null, null, pesoRuta)), 3, 0, camiones, [])]);
        return (cotizacion, cotizacion.RutasOrdenadas[0]);
    }

    private static clsVehiculoLineaSolicitud CamionDe(clsCotizacionSolicitud cotizacion, Guid recomendacion) =>
        cotizacion.Vehiculos.Single(v => v.IdRecomendacionVehiculo == recomendacion);

    // ------------------------------------------------------------------ Apoyo

    /// <summary>Ruta 1 (Buenaventura → Bogotá) con un camión por cada (vehículo, carrocería, peso), en ese orden de creación.</summary>
    private static (clsCotizacionSolicitud Cotizacion, clsLineaSolicitud Ruta) Ruta(params (string? Vehiculo, string? Carroceria, decimal Peso)[] camiones)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-CAM-1" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion }, [LineaCaptura(camiones[0])], [0], 3);
        var ruta = cotizacion.RutasOrdenadas[0];
        foreach (var camion in camiones.Skip(1))
        {
            ruta.AgregarVehiculo(camion.Vehiculo, camion.Carroceria, camion.Peso);
        }

        return (cotizacion, ruta);
    }

    /// <summary><paramref name="numero"/> rutas idénticas (Buenaventura → Bogotá), cada una con un 3S3 de 10 t.</summary>
    private static clsCotizacionSolicitud Solicitud(int numero)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-CAM-2" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [.. Enumerable.Range(0, numero).Select(_ => LineaCaptura(("3S3", null, 10_000m)))], [.. Enumerable.Range(0, numero).Select(i => (int?)i)], 3);
        return cotizacion;
    }

    private static clsLineaSolicitudCosteo LineaCaptura((string? Vehiculo, string? Carroceria, decimal Peso) camion) => new()
    {
        Origen = "Buenaventura",
        Destino = "Bogotá, D.C.",
        OrigenMunicipioId = 76109,
        DestinoMunicipioId = 11001,
        Tipologia = "Contenedor",
        ClaseViaje = "Contenedor",
        ClaseContenedor = "CNT40.HC",
        HorasCargue = 6m,
        HorasDescargue = 5m,
        ValorMercancia = 90_000_000m,
        TipoVehiculo = camion.Vehiculo,
        Carroceria = camion.Carroceria,
        PesoKg = camion.Peso,
    };

    /// <summary>Los camiones de la solicitud como los ve el emparejador, en el orden en que viajan a Nexo.</summary>
    private static IReadOnlyList<clsCamionParaEmparejar> Camiones(clsCotizacionSolicitud cotizacion) =>
        [.. cotizacion.RutasOrdenadas.SelectMany(r => r.VehiculosOrdenados.Select(v => clsCamionParaEmparejar.Desde(r, v)))];

    private static clsVehiculoLineaSolicitud Camion(clsCotizacionSolicitud cotizacion, decimal peso) => cotizacion.Vehiculos.Single(v => v.PesoKg == peso);

    /// <summary>Línea de Nexo de la ruta como la normaliza Nexo (mayúsculas, sin tildes).</summary>
    private static clsLineaSolicitudNexo Linea(string id, clsLineaSolicitud ruta, decimal peso, string? vehiculo, string? carroceria) =>
        new(id, clsEmparejadorLineasNexo.Normalizar($"{ruta.OrigenNombre} → {ruta.DestinoNombre}"), "Contenedor", peso, vehiculo, carroceria);

    private static IEnumerable<IReadOnlyList<T>> Permutaciones<T>(IReadOnlyList<T> elementos)
    {
        if (elementos.Count <= 1)
        {
            yield return elementos;
            yield break;
        }

        for (var i = 0; i < elementos.Count; i++)
        {
            var indice = i;
            foreach (var resto in Permutaciones<T>([.. elementos.Where((_, j) => j != indice)]))
            {
                yield return [elementos[i], .. resto];
            }
        }
    }
}
