using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using SKKPedigree.Data.Models;

namespace SKKPedigree.Data
{
    public class RelationRepository
    {
        private readonly Database _db;
        private readonly DogRepository _dogRepo;

        public RelationRepository(Database db, DogRepository dogRepo)
        {
            _db = db;
            _dogRepo = dogRepo;
        }

        /// <summary>
        /// Walks up both family trees and returns the intersection of ancestor sets.
        /// </summary>
        public async Task<List<DogRecord>> FindCommonAncestorsAsync(string dogIdA, string dogIdB)
        {
            var ancestorsA = new HashSet<string>(await _dogRepo.GetAncestorIdsAsync(dogIdA, 10));
            var ancestorsB = new HashSet<string>(await _dogRepo.GetAncestorIdsAsync(dogIdB, 10));

            var common = ancestorsA.Intersect(ancestorsB).ToList();
            var result = new List<DogRecord>();
            foreach (var id in common)
            {
                var dog = await _dogRepo.GetByIdAsync(id);
                if (dog != null) result.Add(dog);
            }
            return result;
        }

        /// <summary>
        /// Calculates Wright's inbreeding coefficient F(I) for a dog.
        /// F(I) = Σ [(0.5)^(n1+n2+1) * (1 + F(A))]
        /// where the sum is over all common ancestors A of the two parents,
        /// n1 = steps from sire to A, n2 = steps from dam to A.
        /// </summary>
        public async Task<double> CalculateInbreedingCoefficientAsync(string dogId)
        {
            var dog = await _dogRepo.GetByIdAsync(dogId);
            if (dog == null || dog.FatherId == null || dog.MotherId == null)
                return 0.0;

            // Build ancestor path maps for sire and dam
            var sireAncestors = new Dictionary<string, List<int>>();
            var damAncestors = new Dictionary<string, List<int>>();

            await BuildAncestorStepsAsync(dog.FatherId, 1, sireAncestors, 10);
            await BuildAncestorStepsAsync(dog.MotherId, 1, damAncestors, 10);

            double f = 0.0;
            var commonAncestors = sireAncestors.Keys.Intersect(damAncestors.Keys);

            foreach (var ancestorId in commonAncestors)
            {
                var ancestorF = await CalculateInbreedingCoefficientAsync(ancestorId);
                foreach (var n1 in sireAncestors[ancestorId])
                    foreach (var n2 in damAncestors[ancestorId])
                        f += System.Math.Pow(0.5, n1 + n2 + 1) * (1.0 + ancestorF);
            }

            return f;
        }

        private async Task BuildAncestorStepsAsync(
            string dogId, int steps,
            Dictionary<string, List<int>> map,
            int maxDepth)
        {
            if (steps > maxDepth) return;

            if (!map.TryGetValue(dogId, out var list))
            {
                list = new List<int>();
                map[dogId] = list;
            }
            list.Add(steps);

            var dog = await _db.Connection.QueryFirstOrDefaultAsync<DogRecord>(
                "SELECT Id, FatherId, MotherId FROM Dog WHERE Id = @id", new { id = dogId });

            if (dog == null) return;
            if (!string.IsNullOrEmpty(dog.FatherId))
                await BuildAncestorStepsAsync(dog.FatherId, steps + 1, map, maxDepth);
            if (!string.IsNullOrEmpty(dog.MotherId))
                await BuildAncestorStepsAsync(dog.MotherId, steps + 1, map, maxDepth);
        }
    }
}
